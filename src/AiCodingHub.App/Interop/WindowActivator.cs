using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using AiCodingHub.Infrastructure.System;

namespace AiCodingHub.App.Interop;

/// <summary>
/// Brings the terminal window that hosts a Claude Code session to the front: walks up from the
/// session's claude.exe PID through its parent processes to the first one owning a visible
/// top-level window (the terminal, VS Code, …). When that process owns several windows — every
/// VS Code window belongs to one Code.exe — the one whose title names the session's folder wins
/// (<see cref="WindowTitleMatcher"/>), else the topmost. Tabs inside one terminal window cannot be
/// told apart, so the window comes forward but the tab may not change. Best-effort; never throws.
/// </summary>
public static class WindowActivator
{
    /// <param name="pid">The session's claude.exe.</param>
    /// <param name="workspace">The session's cwd (its leaf names the VS Code window).</param>
    /// <param name="repo">The session's repo / group name (fallback title hint for worktrees).</param>
    public static bool TryFocusSession(int pid, string? workspace = null, string? repo = null)
    {
        try
        {
            var parents = new ProcessInspector().ParentMap();
            var windows = VisibleTopLevelWindowsByPid();
            var hints = WindowTitleMatcher.FolderHints(workspace, repo);

            var current = pid;
            for (var depth = 0; depth < 32; depth++)
            {
                if (windows.TryGetValue(current, out var owned))
                {
                    var titles = owned.ConvertAll(w => w.Title);
                    var i = WindowTitleMatcher.Pick(titles, hints);
                    return Activate(owned[Math.Max(i, 0)].Hwnd);
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

    /// <summary>pid → its visible, titled, unowned top-level windows, in z-order (topmost first).</summary>
    private static Dictionary<int, List<(IntPtr Hwnd, string Title)>> VisibleTopLevelWindowsByPid()
    {
        var map = new Dictionary<int, List<(IntPtr, string)>>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
            {
                var len = GetWindowTextLength(hwnd);
                if (len > 0)
                {
                    var sb = new StringBuilder(len + 1);
                    GetWindowText(hwnd, sb, sb.Capacity);
                    GetWindowThreadProcessId(hwnd, out var p);
                    if (!map.TryGetValue((int)p, out var list))
                    {
                        map[(int)p] = list = [];
                    }
                    list.Add((hwnd, sb.ToString()));
                }
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
