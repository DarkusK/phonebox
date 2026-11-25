using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

public sealed record BackseatMember(int Id, string Name, bool Me, bool Streaming, bool Watching, bool InVoice,
    bool Speaking, bool MicMuted, bool Deafened, int? Volume);
public sealed record BackseatRoom(string Code, string Label, bool Open, bool Background);
/// <param name="Viewers">Only the viewers' names: their network numbers stay on the computer.</param>
public sealed record BackseatLive(long SinceSecs, string? Quality, string? Encoder, string? SourceKind, bool Background,
    IReadOnlyList<string> Viewers, long? SourceId = null);
public sealed record BackseatWatch(int Streamer, string? Name, bool Muted, int? Volume, bool Fullscreen);
/// <param name="State">ok; off (Backseat's remote control is off, or Backseat was never started); down (Backseat is
/// not running or not answering); token (Backseat refused the token).</param>
public sealed record BackseatSnapshot(string State, string? Error, bool Joining, bool Reconnecting,
    string? RoomCode, string? RoomTitle, IReadOnlyList<BackseatMember> Members,
    bool InVoice, bool VoiceHere, bool MicMuted, bool Deafened, bool PushToTalk,
    BackseatLive? Live, IReadOnlyList<BackseatWatch> Watching, IReadOnlyList<BackseatRoom> Rooms, long Updated)
{
    public static BackseatSnapshot Down(string state, string? error = null, IReadOnlyList<BackseatRoom>? rooms = null) =>
        new(state, error, false, false, null, null, [], false, false, false, false, false, null, [], rooms ?? [], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
public sealed record BackseatSource(string Kind, long Id, string Name, string? Detail, bool Primary);

/// <summary>
/// Backseat (P2P game streaming and voice rooms, github.com/ArtemNA/backseat) through its local HTTP API on this
/// computer. The address and the token are read from Backseat's own settings file, so nothing has to be copied over,
/// and the token never goes to the phone: the phone talks to the agent, the agent to 127.0.0.1. The status is asked for
/// only while a phone shows a Backseat widget, twice a second (who is speaking); the saved rooms every 10 seconds.
/// Buttons work only when a Backseat widget allows control: the large widget everything, the bar only what it shows
/// (microphone, sound, leaving voice, stopping your stream).
/// </summary>
public sealed class BackseatMonitor(ConfigStore config, ILogger<BackseatMonitor> log) : BackgroundService
{
    internal static readonly string[] BarActions = ["mute", "deafen", "voice-leave", "live-stop"];
    private static readonly string[] Actions = ["mute", "deafen", "voice-join", "voice-leave", "join", "room-leave",
        "live-start", "live-stop", "watch", "unwatch", "watch-mute", "watch-full"];

    internal string StatePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backseat", "state.json");

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = Timeout.InfiniteTimeSpan };

    private volatile BackseatSnapshot _snap = BackseatSnapshot.Down("down") with { Updated = 0 };
    private IReadOnlyList<BackseatRoom> _rooms = [];
    private DateTime _roomsAt = DateTime.MinValue;
    private long _lastRequest;
    private (DateTime Stamp, string? Base, string? Token, bool Enabled) _cred;
    private readonly SemaphoreSlim _act = new(1, 1);

    public BackseatSnapshot Snapshot()
    {
        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        return _snap;
    }

    public async Task<BackseatSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
        if (_snap.Updated == 0) await Task.WhenAny(Refresh(ct), Task.Delay(2500, ct));
        return _snap;
    }

    // ---- who may do what ----

    private static bool Control(AgentConfig cfg, string type) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type == type && w.Bool("control") == true);
    /// <summary>The large widget's Control allows everything; the bar's only what the bar shows.</summary>
    public static bool ControlAllowed(AgentConfig cfg, string? action = null) =>
        Control(cfg, "backseat") || (action is null || BarActions.Contains(action)) && Control(cfg, "backseat-bar");
    public static bool WidgetControl(AgentConfig cfg) => Control(cfg, "backseat");
    /// <summary>Whether a Backseat widget is on an enabled page: nothing is asked of Backseat otherwise.</summary>
    public static bool Wanted(AgentConfig cfg) =>
        cfg.Widgets(enabledPagesOnly: true).Any(w => w.Type is "backseat" or "backseat-bar");

    // ---- polling ----

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _lastRequest) < 10_000)
            {
                try { await Refresh(ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogDebug("backseat: {Msg}", ex.Message); }
                await Task.Delay(_snap.State == "ok" ? 500 : 3000, ct);
            }
            else await Task.Delay(500, ct);
        }
    }

    private async Task Refresh(CancellationToken ct)
    {
        if (!Wanted(config.Get())) { _snap = BackseatSnapshot.Down("off"); return; }
        var (state, status, error) = await Call(HttpMethod.Get, "status", null, TimeSpan.FromSeconds(4), ct);
        if (state != "ok") { _snap = BackseatSnapshot.Down(state, error); return; }
        if (DateTime.UtcNow - _roomsAt > TimeSpan.FromSeconds(10) || _rooms.Count == 0)
        {
            var (rs, rooms, _) = await Call(HttpMethod.Get, "rooms", null, TimeSpan.FromSeconds(4), ct);
            if (rs == "ok") { _rooms = ParseRooms(rooms); _roomsAt = DateTime.UtcNow; }
        }
        _snap = Parse(status, _rooms);
    }

    /// <summary>One call to Backseat's API: (state, answer, error text). The state is ok, off, down or token.</summary>
    private async Task<(string State, JsonNode? Body, string? Error)> Call(HttpMethod method, string path, JsonObject? body, TimeSpan timeout, CancellationToken ct)
    {
        var cred = Credentials();
        if (!cred.Enabled || cred.Base is null || cred.Token is null) return ("off", null, null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var req = new HttpRequestMessage(method, cred.Base + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Token);
            if (method == HttpMethod.Post) req.Content = new StringContent((body ?? []).ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, cts.Token);
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                _cred = default; // a new token in Backseat: read its settings again
                return ("token", null, null);
            }
            var text = await res.Content.ReadAsStringAsync(cts.Token);
            JsonNode? json = null;
            try { json = JsonNode.Parse(text); } catch (JsonException) { }
            if (json?["ok"]?.GetValue<bool>() == true) return ("ok", json, null);
            return ("ok", json, Str(json?["error"]) ?? $"Backseat answered {(int)res.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("down", null, "Backseat did not answer in time");
        }
        catch (HttpRequestException)
        {
            return ("down", null, null);
        }
    }

    /// <summary>The API's address and token from Backseat's settings (settings.api), read again whenever the file changes.</summary>
    private (DateTime Stamp, string? Base, string? Token, bool Enabled) Credentials()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(StatePath);
            if (_cred.Stamp == stamp && stamp != default) return _cred;
            var api = File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath))?["settings"]?["api"] : null;
            var port = api?["port"] is JsonValue p && p.TryGetValue<int>(out var n) && n is > 0 and < 65536 ? n : 47820;
            var token = Str(api?["token"]);
            var enabled = api?["enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && on;
            return _cred = (stamp, $"http://127.0.0.1:{port}/api/v1/", string.IsNullOrWhiteSpace(token) ? null : token, enabled);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return _cred = default; // the file is being written: the next pass reads it again
        }
    }

    // ---- reading Backseat's answers ----

    internal static IReadOnlyList<BackseatRoom> ParseRooms(JsonNode? json) =>
        (json?["rooms"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => Str(r["code"]) is { } code
                ? new BackseatRoom(code, Str(r["label"]) ?? Str(r["title"]) ?? code, Bool(r["open"]), Bool(r["background"]))
                : null)
            .OfType<BackseatRoom>().ToList();

    internal static BackseatSnapshot Parse(JsonNode? s, IReadOnlyList<BackseatRoom> rooms)
    {
        var room = s?["room"] as JsonObject;
        var code = Str(room?["code"]);
        var members = (room?["members"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(m => Int(m["id"]) is not null)
            .Select(m => new BackseatMember(Int(m["id"])!.Value, Str(m["name"]) ?? "?", Bool(m["me"]), Bool(m["streaming"]),
                Bool(m["watching"]), Bool(m["in_voice"]), Bool(m["speaking"]), Bool(m["mic_muted"]), Bool(m["deafened"]), Int(m["volume"])))
            .ToList();
        var voice = s?["voice"] as JsonObject;
        var inVoice = Bool(voice?["joined"]);
        BackseatLive? live = null;
        if (s?["live"] is JsonObject l)
        {
            var liveRoom = Str(l["room"]);
            live = new(Long(l["since_secs"]) ?? 0, Str(l["quality"]), Str(l["encoder"]), Str(l["settings"]?["source"]?["kind"]),
                liveRoom is not null && liveRoom != code,
                (l["viewers"] as JsonArray ?? []).OfType<JsonObject>().Select(v => Str(v["name"])).OfType<string>().ToList(),
                Long(l["settings"]?["source"]?["id"]));
        }
        var watching = (s?["watching"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(w => Int(w["streamer"]) is not null)
            .Select(w => new BackseatWatch(Int(w["streamer"])!.Value, Str(w["name"]), Bool(w["muted"]), Int(w["volume"]), Bool(w["fullscreen"])))
            .ToList();
        return new("ok", null, Bool(s?["joining"]), Bool(s?["reconnecting"]) || Bool(room?["reconnecting"]),
            code, Str(room?["title"]), members, inVoice, inVoice && code is not null && Str(voice?["room"]) == code,
            Bool(voice?["mic_muted"]), Bool(voice?["deafened"]), Bool(voice?["push_to_talk"]),
            // the room list gives the open room's code as its label when it has no saved name: the status knows its title
            live, watching, rooms.Select(r => r with { Open = r.Code == code, Label = r.Code == code && r.Label == r.Code && Str(room?["title"]) is { } t ? t : r.Label }).ToList(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static long? Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? (long)d : null;
    private static int? Int(JsonNode? n) => Long(n) is { } l and >= int.MinValue and <= int.MaxValue ? (int)l : null;

    // ---- the buttons ----

    /// <summary>What can be streamed: monitors and windows (titles only while the large widget may start a stream).</summary>
    public async Task<(IReadOnlyList<BackseatSource>? Sources, string? Error)> SourcesAsync(CancellationToken ct)
    {
        if (!WidgetControl(config.Get())) return (null, "turned off in the widget's settings");
        var (state, json, error) = await Call(HttpMethod.Get, "sources", null, TimeSpan.FromSeconds(6), ct);
        if (state != "ok" || error is not null) return (null, error ?? state);
        var list = new List<BackseatSource>();
        foreach (var m in (json?["monitors"] as JsonArray ?? []).OfType<JsonObject>())
            if (Long(m["id"]) is { } id)
                list.Add(new("monitor", id, Str(m["name"]) ?? "Monitor", $"{Int(m["width"])}×{Int(m["height"])}", Bool(m["primary"])));
        foreach (var w in (json?["windows"] as JsonArray ?? []).OfType<JsonObject>())
            if (Long(w["id"]) is { } id && !Bool(w["minimized"]))
                list.Add(new("window", id, Str(w["title"]) ?? Str(w["exe"]) ?? "Window", Str(w["exe"]), false));
        // the main screen first, then the other screens, then the windows as Backseat lists them
        return ([.. list.OrderBy(s => s.Kind == "monitor" ? (s.Primary ? 0 : 1) : 2)], null);
    }

    /// <summary>Runs a button; null when done, else what went wrong. The status Backseat answers with is taken as the new state.</summary>
    public async Task<string?> ActAsync(string action, int? id, string? room, string? kind, long? source)
    {
        if (!Actions.Contains(action)) return "unknown action";
        if (!ControlAllowed(config.Get(), action)) return "control from the phone is turned off in the widget's settings";
        if (!await _act.WaitAsync(TimeSpan.FromSeconds(40))) return "another action is still running";
        try
        {
            Interlocked.Exchange(ref _lastRequest, Environment.TickCount64);
            var snap = _snap;
            string? err;
            switch (action)
            {
                case "mute": err = await Post("voice/mute", null, 8); break;
                case "deafen": err = await Post("voice/deafen", null, 8); break;
                case "voice-join": err = await Post("voice/join", null, 15); break;
                case "voice-leave": err = await Post("voice/leave", null, 8); break;
                case "room-leave": err = await Post("room/leave", null, 15); break;
                case "live-stop": err = await Post("live/stop", null, 10); break;
                case "join":
                    // a saved room (only one Backseat listed), then its voice chat: the room list's tap, as in Discord
                    if (room is null || !snap.Rooms.Any(r => r.Code == room)) return "unknown room";
                    err = snap.RoomCode == room ? null : await Post($"rooms/{Uri.EscapeDataString(room)}/open", null, 35);
                    if (err is null && !(snap.RoomCode == room && snap.VoiceHere)) err = await Post("voice/join", null, 15);
                    break;
                case "live-start":
                    JsonObject? body = null;
                    if (kind is "monitor" or "window")
                        body = new JsonObject { ["source"] = source is { } s ? new JsonObject { ["kind"] = kind, ["id"] = s } : new JsonObject { ["kind"] = kind } };
                    err = await Post(snap.Live is null ? "live/start" : "live/update", body, 20);
                    break;
                default: // the watch actions: only a member of the open room
                    if (id is not { } m || !snap.Members.Any(x => x.Id == m && !x.Me)) return "unknown member";
                    err = action switch
                    {
                        "watch" => await Post($"watch/{m}", null, 20),
                        "unwatch" => await Post($"watch/{m}/stop", null, 8),
                        "watch-mute" => await Post($"watch/{m}/mute", null, 8),
                        _ => await Post($"watch/{m}/window", new JsonObject { ["action"] = "toggle_fullscreen" }, 8),
                    };
                    break;
            }
            log.LogInformation("backseat {Action}: {Result}", action, err ?? "ok");
            return err;
        }
        finally { _act.Release(); }
    }

    private async Task<string?> Post(string path, JsonObject? body, int seconds)
    {
        var (state, json, error) = await Call(HttpMethod.Post, path, body, TimeSpan.FromSeconds(seconds), CancellationToken.None);
        if (state != "ok") return state switch
        {
            "off" => "Backseat's remote control is off (Backseat › Settings › Remote control)",
            "token" => "Backseat refused the token",
            _ => error ?? "Backseat is not running",
        };
        if (error is null && json is JsonObject o && o.ContainsKey("voice")) _snap = Parse(json, _rooms);
        return error;
    }
}
