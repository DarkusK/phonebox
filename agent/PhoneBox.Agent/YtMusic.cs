using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>
/// YouTube Music through the desktop app's API Server plugin (th-ch / pear-devs "YouTube Music", on this computer):
/// the song, player state and queue for the phone, and its buttons. The app asks the user once to allow the client
/// "phonebox"; after that a token is issued without asking. Nothing leaves the computer except the cover pictures,
/// which are fetched from Google's image hosts through <see cref="ImageAsync"/>.
/// </summary>
public sealed class YtMusic(ILogger<YtMusic> log)
{
    public const int Port = 26538;
    private const string ClientId = "phonebox";
    private static readonly string Base = $"http://127.0.0.1:{Port}";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private string? _token;
    private JsonArray? _queue;
    private long _queueAt;

    /// <summary>Everything the widget shows, or why it can't.</summary>
    public async Task<object> SnapshotAsync(CancellationToken ct)
    {
        try
        {
            var song = await GetAsync("/api/v1/song", ct);
            var like = GetAsync("/api/v1/like-state", ct);
            var vol = GetAsync("/api/v1/volume", ct);
            var rep = GetAsync("/api/v1/repeat-mode", ct);
            var shuf = GetAsync("/api/v1/shuffle", ct);
            // the queue changes rarely and is the biggest answer: every 3 s at most, and at once after a skip
            if (_queue is null || Environment.TickCount64 - _queueAt > 3000) { _queue = Queue(await GetAsync("/api/v1/queue", ct)); _queueAt = Environment.TickCount64; }
            await Task.WhenAll(like, vol, rep, shuf);
            Heard(Str(song?["artist"]));
            return new
            {
                connected = true,
                song = song is null ? null : new
                {
                    title = Str(song["title"]), artist = Str(song["artist"]), album = Str(song["album"]), cover = Str(song["imageSrc"]),
                    duration = Num(song["songDuration"]), elapsed = Num(song["elapsedSeconds"]), paused = song["isPaused"]?.GetValue<bool>() ?? true,
                    videoId = Str(song["videoId"]),
                },
                like = Str(like.Result?["state"]),
                volume = Num(vol.Result?["state"]), muted = vol.Result?["isMuted"]?.GetValue<bool>() ?? false,
                repeat = Str(rep.Result?["mode"]), shuffle = shuf.Result?["state"]?.GetValue<bool>() ?? false,
                queue = _queue,
            };
        }
        catch (HttpRequestException) { return new { connected = false, error = "YouTube Music is not running, or its API Server plugin is off" }; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new { connected = false, error = "YouTube Music does not answer" }; }
        catch (UnauthorizedAccessException e) { return new { connected = false, error = e.Message }; }
    }

    /// <summary>
    /// A button: play / pause, skip, like, repeat, shuffle, volume, mute, seek; a jump, move or removal in the queue
    /// (value = index, to = new index); a song put in the queue (id = videoId): played now, next, or at the end.
    /// </summary>
    public async Task<string?> ActAsync(string action, double? value, string? id, double? to, CancellationToken ct)
    {
        try
        {
            switch (action)
            {
                case "play-now" when !string.IsNullOrEmpty(id):
                    // the app has no "play this": the song goes right after the current one and the player skips to it
                    if (await Call(HttpMethod.Post, "/api/v1/queue", new { videoId = id, insertPosition = "INSERT_AFTER_CURRENT_VIDEO" }, ct) is { } e1) return e1;
                    return await Call(HttpMethod.Post, "/api/v1/next", null, ct);
                case "add-next" when !string.IsNullOrEmpty(id):
                    return await Call(HttpMethod.Post, "/api/v1/queue", new { videoId = id, insertPosition = "INSERT_AFTER_CURRENT_VIDEO" }, ct);
                case "add-end" when !string.IsNullOrEmpty(id):
                    return await Call(HttpMethod.Post, "/api/v1/queue", new { videoId = id, insertPosition = "INSERT_AT_END" }, ct);
                case "remove" when value is { } r:
                    return await Call(HttpMethod.Delete, $"/api/v1/queue/{(int)r}", null, ct);
                case "move" when value is { } from && to is { } dest:
                    return await Call(HttpMethod.Patch, $"/api/v1/queue/{(int)from}", new { toIndex = (int)dest }, ct);
                case "clear":
                    return await Call(HttpMethod.Delete, "/api/v1/queue", null, ct);
            }
            (HttpMethod m, string path, object? body) = action switch
            {
                "toggle-play" or "next" or "previous" or "like" or "dislike" or "shuffle" or "toggle-mute" => (HttpMethod.Post, "/api/v1/" + action, (object?)null),
                "repeat" => (HttpMethod.Post, "/api/v1/switch-repeat", (object?)new { iteration = 1 }),
                "volume" when value is { } v => (HttpMethod.Post, "/api/v1/volume", (object?)new { volume = Math.Clamp(v, 0, 100) }),
                "seek" when value is { } s => (HttpMethod.Post, "/api/v1/seek-to", (object?)new { seconds = Math.Max(0, s) }),
                "jump" when value is { } i => (HttpMethod.Patch, "/api/v1/queue", (object?)new { index = (int)i }),
                _ => (HttpMethod.Get, "", (object?)null),
            };
            if (path == "") return "unknown action";
            return await Call(m, path, body, ct);
        }
        catch (HttpRequestException) { return "YouTube Music is not running, or its API Server plugin is off"; }
        catch (UnauthorizedAccessException e) { return e.Message; }
        finally { _queue = null; } // whatever the button did, the queue is read again on the next poll
    }

    private async Task<string?> Call(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        using var res = await SendAsync(m, path, body, ct);
        return res.IsSuccessStatusCode ? null : $"YouTube Music answered {(int)res.StatusCode}";
    }

    // ---------------------------------------------------------------- search and "for you"
    // The app's search runs as the signed-in user, so its answers are personal: a search for an artist brings that
    // artist's "listen again" shelf too. Filters are YouTube Music's own chips; their codes are learnt from the first
    // answer and picked by their stable start (songs, albums, artists, videos).
    private static readonly Dictionary<string, string> FilterPrefix = new()
    {
        ["songs"] = "EgWKAQII", ["albums"] = "EgWKAQIY", ["artists"] = "EgWKAQIg", ["videos"] = "EgWKAQIQ",
    };
    private readonly ConcurrentDictionary<string, string> _filters = new();
    private readonly ConcurrentDictionary<string, (long At, JsonObject Result)> _searchCache = new();

    /// <summary>Search: sections of songs, videos, albums and artists (playlists, podcasts and profiles can't be played through the app's API, so they are left out).</summary>
    public async Task<object> SearchAsync(string query, string? filter, CancellationToken ct)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return new { sections = Array.Empty<object>() };
        if (query.Length > 200) query = query[..200];
        try { return await SearchNodeAsync(query, filter, ct); }
        catch (HttpRequestException) { return new { error = "YouTube Music is not running, or its API Server plugin is off" }; }
        catch (UnauthorizedAccessException e) { return new { error = e.Message }; }
    }

    private async Task<JsonObject> SearchNodeAsync(string query, string? filter, CancellationToken ct)
    {
        var key = (filter ?? "") + "\n" + query;
        if (_searchCache.TryGetValue(key, out var hit) && Environment.TickCount64 - hit.At < 10 * 60_000) return hit.Result;
        string? param = null;
        if (filter is not null && FilterPrefix.ContainsKey(filter))
        {
            if (!_filters.ContainsKey(filter)) await SearchNodeAsync(query, null, ct); // learn the chip codes first
            _filters.TryGetValue(filter, out param);
        }
        using var res = await SendAsync(HttpMethod.Post, "/api/v1/search", param is null ? new { query } : new { query, @params = param }, ct);
        if (!res.IsSuccessStatusCode) return new JsonObject { ["error"] = $"YouTube Music answered {(int)res.StatusCode}" };
        var raw = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        var result = ParseSearch(raw);
        if (_searchCache.Count > 100) _searchCache.Clear();
        _searchCache[key] = (Environment.TickCount64, result);
        return result;
    }

    private JsonObject ParseSearch(JsonNode? raw)
    {
        var sections = new JsonArray();
        var rest = new List<JsonNode?>();
        void Section(string title, IEnumerable<JsonNode?> renderers)
        {
            var items = new JsonArray();
            foreach (var r in renderers) if (Item(r) is { } it) items.Add(it);
            if (items.Count > 0) sections.Add(new JsonObject { ["title"] = title, ["items"] = items });
        }
        void Walk(JsonNode? n)
        {
            if (n is JsonArray a) { foreach (var v in a) Walk(v); return; }
            if (n is not JsonObject o) return;
            if (o["chipCloudChipRenderer"] is JsonObject chip && Str(chip["navigationEndpoint"]?["searchEndpoint"]?["params"]) is { } p)
            {
                foreach (var (name, prefix) in FilterPrefix) if (p.StartsWith(prefix, StringComparison.Ordinal)) _filters[name] = p;
                return;
            }
            if (o["musicCardShelfRenderer"] is JsonObject card)
            {
                // the top result: the card itself, then the few songs it lists
                var top = new List<JsonNode?> { card };
                foreach (var c in card["contents"]?.AsArray() ?? []) if (c?["musicResponsiveListItemRenderer"] is { } r) top.Add(r);
                Section(Runs(card["header"]?["musicCardShelfHeaderBasicRenderer"]?["title"]) ?? "", top);
                return;
            }
            if (o["musicShelfRenderer"] is JsonObject shelf)
            {
                Section(Runs(shelf["title"]) ?? "", (shelf["contents"]?.AsArray() ?? []).Select(c => c?["musicResponsiveListItemRenderer"]));
                return;
            }
            // results outside a shelf (the "all results" list comes that way): one section of their own, at the end
            if (o["musicResponsiveListItemRenderer"] is JsonObject loose) { rest.Add(loose); return; }
            foreach (var (_, v) in o) Walk(v);
        }
        Walk(raw);
        Section("", rest);
        return new JsonObject { ["sections"] = sections };
    }

    // one result: a song or video (playable: videoId), an album (its first track's videoId and its browse id), an artist
    private static JsonObject? Item(JsonNode? r)
    {
        if (r is not JsonObject o) return null;
        var isCard = o["subtitle"] is not null && o["flexColumns"] is null;
        var title = isCard ? Runs(o["title"]) : Runs(o["flexColumns"]?[0]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]);
        var sub = isCard ? Runs(o["subtitle"]) : string.Join(" ", (o["flexColumns"]?.AsArray() ?? []).Skip(1).Select(c => Runs(c?["musicResponsiveListItemFlexColumnRenderer"]?["text"])).Where(s => !string.IsNullOrEmpty(s)));
        var thumb = Str(o["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"]?.AsArray().LastOrDefault()?["url"]);
        var nav = isCard ? o["title"]?["runs"]?[0]?["navigationEndpoint"] : o["navigationEndpoint"];
        var browse = nav?["browseEndpoint"];
        var page = Str(browse?["browseEndpointContextSupportedConfigs"]?["browseEndpointContextMusicConfig"]?["pageType"]) ?? "";
        var videoId = Str(o["playlistItemData"]?["videoId"]) ?? Str(nav?["watchEndpoint"]?["videoId"])
            ?? Str(o["overlay"]?["musicItemThumbnailOverlayRenderer"]?["content"]?["musicPlayButtonRenderer"]?["playNavigationEndpoint"]?["watchEndpoint"]?["videoId"]);
        var kind = page switch
        {
            "MUSIC_PAGE_TYPE_ALBUM" => "album",
            "MUSIC_PAGE_TYPE_ARTIST" => "artist",
            "" when videoId is not null => "song",
            _ => null, // playlists, podcasts, profiles: the app's API can't play them
        };
        if (kind is null || string.IsNullOrEmpty(title)) return null;
        return new JsonObject { ["kind"] = kind, ["title"] = title, ["sub"] = sub, ["thumb"] = thumb, ["videoId"] = videoId, ["browseId"] = Str(browse?["browseId"]) };
    }

    // "For you" without a plugin: the app offers no home feed, so it is made of what it does offer, as the user: the
    // mix it plays after the current song, and for the artists heard lately their popular songs and the "listen again"
    // shelf a search for them brings.
    private readonly List<string> _artists = [];
    private void Heard(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return;
        lock (_artists) { _artists.Remove(artist); _artists.Insert(0, artist); if (_artists.Count > 8) _artists.RemoveAt(8); }
    }

    public async Task<object> ForYouAsync(CancellationToken ct)
    {
        string[] artists;
        lock (_artists) artists = [.. _artists.Take(3)];
        var sections = new JsonArray();
        foreach (var artist in artists)
        {
            try
            {
                var found = await SearchNodeAsync(artist, null, ct);
                var items = new JsonArray();
                var seen = new HashSet<string>();
                foreach (var s in found["sections"]?.AsArray() ?? [])
                    foreach (var it in s?["items"]?.AsArray() ?? [])
                        if (Str(it?["kind"]) == "song" && Str(it?["videoId"]) is { } v && seen.Add(v) && items.Count < 12) items.Add(it!.DeepClone());
                if (items.Count > 0) sections.Add(new JsonObject { ["title"] = artist, ["artist"] = artist, ["items"] = items });
            }
            catch (HttpRequestException) { return new { error = "YouTube Music is not running, or its API Server plugin is off" }; }
            catch (UnauthorizedAccessException e) { return new { error = e.Message }; }
        }
        return new { sections };
    }

    // cover pictures: only from Google's image hosts, kept for a while (a queue shows the same few again and again)
    private readonly ConcurrentDictionary<string, (byte[] Data, string Type)> _images = new();
    public async Task<(byte[] Data, string Type)?> ImageAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https"
            || !(u.Host.EndsWith(".googleusercontent.com") || u.Host.EndsWith(".ytimg.com") || u.Host.EndsWith(".ggpht.com"))) return null;
        if (_images.TryGetValue(url, out var hit)) return hit;
        try
        {
            using var res = await _http.GetAsync(u, ct);
            if (!res.IsSuccessStatusCode) return null;
            var type = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            if (!type.StartsWith("image/")) return null;
            var data = await res.Content.ReadAsByteArrayAsync(ct);
            if (_images.Count > 200) _images.Clear();
            return _images[url] = (data, type);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var res = await SendAsync(HttpMethod.Get, path, null, ct);
        if (res.StatusCode == HttpStatusCode.NoContent || !res.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    // every request carries the token; a refused one gets a fresh token once (the app may have restarted with a new secret)
    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = _token ?? await TokenAsync(ct);
            using var req = new HttpRequestMessage(m, Base + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) req.Content = JsonContent.Create(body);
            var res = await _http.SendAsync(req, ct);
            if (res.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return res;
            res.Dispose();
            _token = null;
        }
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            if (_token is not null) return _token;
            // allowed once in the app's own dialog: later asks are answered at once; a first ask waits for the user
            using var res = await _http.PostAsync($"{Base}/auth/{ClientId}", null, ct);
            if (res.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("YouTube Music did not allow PhoneBox: allow the client \"phonebox\" in the app");
            res.EnsureSuccessStatusCode();
            var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            _token = Str(j?["accessToken"]) ?? throw new UnauthorizedAccessException("YouTube Music gave no token");
            log.LogInformation("ytmusic: connected");
            return _token;
        }
        finally { _authLock.Release(); }
    }

    // The queue comes as YouTube Music's own renderer tree; the items are its playlistPanelVideoRenderer objects
    // (some wrapped in a playlistPanelVideoWrapperRenderer), in order.
    private static JsonArray Queue(JsonNode? q)
    {
        var list = new JsonArray();
        void Walk(JsonNode? n)
        {
            if (list.Count >= 100) return;
            if (n is JsonObject o)
            {
                if (o["playlistPanelVideoRenderer"] is JsonObject r)
                {
                    list.Add(new JsonObject
                    {
                        ["title"] = Runs(r["title"]), ["artist"] = Runs(r["shortBylineText"]), ["length"] = Runs(r["lengthText"]),
                        ["thumb"] = Str(r["thumbnail"]?["thumbnails"]?.AsArray().LastOrDefault()?["url"]),
                        ["videoId"] = Str(r["videoId"]), ["selected"] = r["selected"]?.GetValue<bool>() ?? false,
                    });
                    return;
                }
                foreach (var (_, v) in o) Walk(v);
            }
            else if (n is JsonArray a) foreach (var v in a) Walk(v);
        }
        Walk(q);
        return list;
    }

    private static string? Runs(JsonNode? n) =>
        n?["runs"] is JsonArray runs ? string.Concat(runs.Select(r => Str(r?["text"]))) : Str(n?["simpleText"]);
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
