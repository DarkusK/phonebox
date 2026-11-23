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

    /// <summary>A button: play / pause, skip, like, repeat, shuffle, volume, mute, seek, or a jump to a queue item.</summary>
    public async Task<string?> ActAsync(string action, double? value, CancellationToken ct)
    {
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
        try
        {
            using var res = await SendAsync(m, path, body, ct);
            if (!res.IsSuccessStatusCode) return $"YouTube Music answered {(int)res.StatusCode}";
            if (action is "next" or "previous" or "jump") _queue = null; // the queue's current item moved
            return null;
        }
        catch (HttpRequestException) { return "YouTube Music is not running, or its API Server plugin is off"; }
        catch (UnauthorizedAccessException e) { return e.Message; }
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
