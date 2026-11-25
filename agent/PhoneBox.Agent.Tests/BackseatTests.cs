using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

namespace PhoneBox.Agent.Tests;

/// <summary>Backseat through its local API: a stand-in Backseat answers on a loopback port.</summary>
public sealed class BackseatTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-backseat-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _server = TestPorts.Listen();
    private readonly List<(string Method, string Path, string Auth, string Body)> _calls = [];
    private readonly Task _serve;
    private string? _openRoom = "K7M2Q9XR4TBA";
    private bool _inVoice;

    public BackseatTests()
    {
        Directory.CreateDirectory(_dir);
        _serve = Serve();
    }

    public void Dispose()
    {
        _server.Close();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private int Port => new Uri(_server.Prefixes.Single()).Port;

    private BackseatMonitor Monitor(string type = "backseat", bool control = true, bool apiEnabled = true, bool pageEnabled = true)
    {
        var state = Path.Combine(_dir, "state.json");
        File.WriteAllText(state, JsonSerializer.Serialize(new
        {
            rooms = Array.Empty<object>(),
            settings = new { api = new { enabled = apiEnabled, lan = false, port = Port, token = Token } },
        }));
        var cfg = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfg, JsonSerializer.Serialize(new
        {
            pages = new object[] {
                new { id = "home", enabled = true, widgets = new object[] { new { id = "c", type = "clock", options = new { } } } },
                new { id = "p", enabled = pageEnabled, widgets = new object[] { new { id = "w", type, options = new { control } } } },
            },
        }));
        return new BackseatMonitor(new ConfigStore(cfg), NullLogger<BackseatMonitor>.Instance) { StatePath = state };
    }

    private string Status() => new JsonObject
    {
        ["ok"] = true,
        ["joining"] = false,
        ["reconnecting"] = false,
        ["room"] = _openRoom is null ? null : new JsonObject
        {
            ["code"] = _openRoom, ["title"] = "Evening games", ["server"] = "wss://example.com/ws", ["me"] = 12,
            ["members"] = new JsonArray(
                new JsonObject { ["id"] = 12, ["name"] = "Alex", ["me"] = true, ["streaming"] = true, ["in_voice"] = _inVoice, ["speaking"] = false, ["mic_muted"] = false, ["deafened"] = false, ["volume"] = null },
                new JsonObject { ["id"] = 15, ["name"] = "Maria", ["me"] = false, ["streaming"] = true, ["in_voice"] = true, ["speaking"] = true, ["mic_muted"] = true, ["deafened"] = false, ["volume"] = 100 }),
        },
        ["voice"] = new JsonObject { ["joined"] = _inVoice, ["room"] = _inVoice ? _openRoom : null, ["mic_muted"] = false, ["deafened"] = false, ["push_to_talk"] = false },
        ["live"] = new JsonObject
        {
            ["since_secs"] = 754, ["quality"] = "1080p60 20000", ["encoder"] = "AV1 · NVENC",
            ["settings"] = new JsonObject { ["source"] = new JsonObject { ["kind"] = "monitor", ["id"] = 65537 } },
            ["viewers"] = new JsonArray(new JsonObject { ["id"] = 15, ["name"] = "Maria", ["rtt_ms"] = 18.0 }),
        },
        ["watching"] = new JsonArray(),
    }.ToJsonString();

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _server.GetContextAsync(); } catch { return; }
            var path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath);
            var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            var auth = ctx.Request.Headers["Authorization"] ?? "";
            lock (_calls) _calls.Add((ctx.Request.HttpMethod, path, auth, body));
            string answer;
            if (auth != "Bearer " + Token) { ctx.Response.StatusCode = 401; answer = "{\"ok\":false,\"error\":\"token\"}"; }
            else if (path == "/api/v1/rooms")
                answer = "{\"ok\":true,\"rooms\":[{\"code\":\"K7M2Q9XR4TBA\",\"label\":\"Evening games\",\"open\":true},{\"code\":\"Duo night\",\"label\":\"Duo night\",\"open\":false}]}";
            else if (path == "/api/v1/rooms/Duo night/open") { _openRoom = "Duo night"; answer = Status(); }
            else if (path == "/api/v1/voice/join") { _inVoice = true; answer = Status(); }
            else if (path == "/api/v1/live/start") { ctx.Response.StatusCode = 409; answer = "{\"ok\":false,\"error\":\"already live\"}"; }
            else answer = Status();
            var bytes = Encoding.UTF8.GetBytes(answer);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private string[] Posts() { lock (_calls) return _calls.Where(c => c.Method == "POST").Select(c => c.Path).ToArray(); }

    [Fact]
    public async Task ReadsTheTokenFromBackseatsSettingsAndKeepsOnlyWhatThePanelShows()
    {
        var mon = Monitor();
        var s = await mon.SnapshotAsync();
        Assert.Equal("ok", s.State);
        Assert.Equal("K7M2Q9XR4TBA", s.RoomCode);
        Assert.Equal(["Alex", "Maria"], s.Members.Select(m => m.Name));
        Assert.True(s.Members[1].Speaking);
        Assert.True(s.Members[1].MicMuted);
        Assert.Equal(754, s.Live!.SinceSecs);
        Assert.Equal(["Maria"], s.Live.Viewers);
        Assert.Equal(2, s.Rooms.Count);
        Assert.True(s.Rooms[0].Open);
        lock (_calls) Assert.All(_calls, c => Assert.Equal("Bearer " + Token, c.Auth));
        // neither the token nor the signaling server reaches the phone
        var json = JsonSerializer.Serialize(s);
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain("wss://", json);
    }

    [Fact]
    public async Task RemoteControlOffInBackseatAsksNothing()
    {
        var s = await Monitor(apiEnabled: false).SnapshotAsync();
        Assert.Equal("off", s.State);
        lock (_calls) Assert.Empty(_calls);
    }

    [Fact]
    public async Task AWrongTokenSaysSo()
    {
        var mon = Monitor();
        var state = Path.Combine(_dir, "state.json");
        File.WriteAllText(state, File.ReadAllText(state).Replace(Token, "ffffffffffffffffffffffffffffffff"));
        Assert.Equal("token", (await mon.SnapshotAsync()).State);
    }

    [Fact]
    public async Task NotRunningIsDown()
    {
        var mon = Monitor();
        _server.Stop();
        Assert.Equal("down", (await mon.SnapshotAsync()).State);
    }

    [Fact]
    public async Task JoiningARoomOpensItThenItsVoice()
    {
        var mon = Monitor();
        await mon.SnapshotAsync();
        Assert.Null(await mon.ActAsync("join", null, "Duo night", null, null));
        Assert.Equal(["/api/v1/rooms/Duo night/open", "/api/v1/voice/join"], Posts());
        var s = mon.Snapshot();
        Assert.Equal("Duo night", s.RoomCode);
        Assert.True(s.VoiceHere);
    }

    [Fact]
    public async Task OnlyASavedRoomIsOpened()
    {
        var mon = Monitor();
        await mon.SnapshotAsync();
        Assert.Equal("unknown room", await mon.ActAsync("join", null, "../settings", null, null));
        Assert.Empty(Posts());
    }

    [Fact]
    public async Task StartingWhileLiveChangesTheRunningStream()
    {
        var mon = Monitor();
        await mon.SnapshotAsync();
        // already live: an update, not a second start
        Assert.Null(await mon.ActAsync("live-start", null, null, "window", 395274));
        Assert.Equal(["/api/v1/live/update"], Posts());
    }

    [Theory]
    [InlineData("backseat", true, "watch", true)]
    [InlineData("backseat", false, "mute", false)]
    [InlineData("backseat-bar", true, "mute", true)]
    [InlineData("backseat-bar", true, "live-stop", true)]
    [InlineData("backseat-bar", true, "join", false)]
    [InlineData("backseat-bar", true, "watch", false)]
    public async Task WhatEachWidgetsControlAllows(string type, bool control, string action, bool allowed)
    {
        var mon = Monitor(type, control);
        await mon.SnapshotAsync();
        var err = await mon.ActAsync(action, 15, "K7M2Q9XR4TBA", null, null);
        if (allowed) Assert.DoesNotContain("turned off", err ?? "");
        else Assert.Contains("turned off", err);
    }

    [Fact]
    public async Task NoWidgetOnAnEnabledPageAsksNothing()
    {
        var s = await Monitor(pageEnabled: false).SnapshotAsync();
        Assert.Equal("off", s.State);
        lock (_calls) Assert.Empty(_calls);
    }

    [Fact]
    public async Task WatchingIsOnlyForAnotherMemberOfTheRoom()
    {
        var mon = Monitor();
        await mon.SnapshotAsync();
        Assert.Equal("unknown member", await mon.ActAsync("watch", 12, null, null, null)); // yourself
        Assert.Equal("unknown member", await mon.ActAsync("watch", 99, null, null, null));
        Assert.Null(await mon.ActAsync("watch", 15, null, null, null));
        Assert.Equal(["/api/v1/watch/15"], Posts());
    }
}
