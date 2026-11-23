using System.Diagnostics;
#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace PhoneBox.Agent;

/// <summary>The app that owns the foreground window, for smart shortcut folders (they open while their app is in front).</summary>
public static class FrontWindow
{
    private static string? _app;
    private static long _at;

    /// <summary>The process name ("Discord", "obs64") of the window in front, or null when it cannot be told; asked at most once a second.</summary>
    public static string? App()
    {
        if (_at != 0 && Environment.TickCount64 - _at < 1000) return _app;
        _app = Probe();
        _at = Environment.TickCount64;
        return _app;
    }

#if WINDOWS
    private static string? Probe()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0) return null;
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; } catch { return null; }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
#else
    // macOS and Linux would need the same probes as the rules' "app in front" condition; smart folders are Windows-only for now
    private static string? Probe() => null;
#endif
}
