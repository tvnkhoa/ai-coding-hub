using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AiCodingHub.Infrastructure.System;

namespace AiCodingHub.App.Interop;

/// <summary>
/// Brings the terminal window that hosts a Claude Code session to the front: walks up from the
/// session's claude.exe PID through its parent processes and activates the first visible
/// top-level window owned by one of them (the terminal, VS Code, …). Tabs inside one terminal
/// window cannot be told apart, so the window comes forward but the tab may not change.
/// Best-effort; never throws.
/// </summary>
public static class WindowActivator
{
    public static bool TryFocusSession(int pid)
    {
        try
        {
            var parents = new ProcessInspector().ParentMap();
            var windows = VisibleTopLevelWindowsByPid();

            var current = pid;
            for (var depth = 0; depth < 32; depth++)
            {
                if (windows.TryGetValue(current, out var hwnd))
                {
                    return Activate(hwnd);
                }

                if (!parents.TryGetValue(current, out var parent) || parent <= 0 || parent == current)
                {
                    return false;
                }
                current = parent;
            }
        }
        catch
        {
            // best-effort
        }

        return false;
    }

    private static bool Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SW_RESTORE);
        }
        return SetForegroundWindow(hwnd);
    }

    /// <summary>pid → its first visible, titled, unowned top-level window.</summary>
    private static Dictionary<int, IntPtr> VisibleTopLevelWindowsByPid()
    {
        var map = new Dictionary<int, IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero && GetWindowTextLength(hwnd) > 0)
            {
                GetWindowThreadProcessId(hwnd, out var p);
                map.TryAdd((int)p, hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return map;
    }

    private const int SW_RESTORE = 9;
    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
