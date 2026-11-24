using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoneBox.Agent;

/// <summary>
/// Discord's public list of the games it detects, which are the apps it can share directly. Only the Windows executable
/// names are kept, in a cache file next to the log, and the list is read again once a day while screen share is in use.
/// Nothing about this computer is sent: it is a plain GET of a public URL.
/// </summary>
internal static class DiscordDetectable
{
    internal const string Url = "https://discord.com/api/v9/applications/detectable";
    internal static string CachePath = Path.Combine(AppPaths.LocalDir, "discord-detectable.json"); // internal: tests use their own
    internal static TimeSpan MaxAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly object Lock = new();
    private static HashSet<string>? _names;
    private static DateTime _at, _nextTry;
    private static bool _loaded, _refreshing;

    /// <summary>The executable names, or null while none is known (then nothing is filtered). An old list is read again in the background.</summary>
    public static HashSet<string>? Names(ILogger log)
    {
        lock (Lock)
        {
            if (!_loaded) { _loaded = true; Load(); }
            var now = DateTime.UtcNow;
            if ((_names is null || now - _at > MaxAge) && now >= _nextTry && !_refreshing)
            {
                _refreshing = true;
                _ = Task.Run(() => Refresh(log));
            }
            return _names;
        }
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var doc = JsonNode.Parse(File.ReadAllText(CachePath));
            var names = doc?["names"]?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (names is { Count: > 0 } && doc?["at"]?.GetValue<DateTime>() is { } at) { _names = names; _at = at; }
        }
        catch { } // a damaged cache is read again from Discord
    }

    private static async Task Refresh(ILogger log)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync(Url);
            using var doc = await JsonDocument.ParseAsync(stream);
            var names = Parse(doc);
            if (names.Count < 100) throw new InvalidDataException($"only {names.Count} games");
            var at = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new { at, names = names.Order(StringComparer.Ordinal) }));
            lock (Lock) { _names = names; _at = at; }
            log.LogInformation("discord: {Count} detectable game executables", names.Count);
        }
        catch (Exception ex)
        {
            log.LogInformation("discord: the list of detectable games could not be read ({Msg}); trying again in an hour", ex.Message);
            lock (Lock) _nextTry = DateTime.UtcNow + RetryAfter;
        }
        finally { lock (Lock) _refreshing = false; }
    }

    /// <summary>The Windows executables of the list, lower case with forward slashes; launchers are left out (Discord doesn't share them as games).</summary>
    internal static HashSet<string> Parse(JsonDocument doc)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return names;
        foreach (var app in doc.RootElement.EnumerateArray())
        {
            if (!app.TryGetProperty("executables", out var exes) || exes.ValueKind != JsonValueKind.Array) continue;
            foreach (var exe in exes.EnumerateArray())
            {
                if (exe.TryGetProperty("os", out var os) && os.GetString() != "win32") continue;
                if (exe.TryGetProperty("is_launcher", out var launcher) && launcher.ValueKind == JsonValueKind.True) continue;
                var name = exe.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (Normalize(name?.TrimStart('>')) is { Length: > 0 } clean) names.Add(clean);
            }
        }
        return names;
    }

    /// <summary>Whether an executable is in the list: its path ends with one of the names (a file name, or folders and the file name).</summary>
    internal static bool Matches(HashSet<string> names, string? path)
    {
        if (Normalize(path) is not { Length: > 0 } p) return false;
        if (names.Contains(p)) return true;
        for (var i = p.LastIndexOf('/'); i >= 0; i = i > 0 ? p.LastIndexOf('/', i - 1) : -1)
            if (names.Contains(p[(i + 1)..])) return true;
        return false;
    }

    private static string? Normalize(string? s) => s?.Trim().Replace('\\', '/').ToLowerInvariant();
}
