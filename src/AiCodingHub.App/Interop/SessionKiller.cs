using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace AiCodingHub.App.Interop;

/// <summary>
/// Ends one Claude Code session from the dashboard's right-click menu: asks the owner to confirm
/// (naming repo + short id), then kills that session's claude.exe and its whole process tree
/// (taskkill /T semantics, so its MCP servers and tool children go too). Refuses anything that is
/// not a running claude.exe, and never the HUD itself. Logged; never throws.
/// </summary>
public static class SessionKiller
{
    /// <summary>
    /// Called from the row's context-menu click. The dialog is posted, not shown inline: a modal
    /// opened while the ContextMenu is still closing (menu mode, mouse capture) is dismissed at once.
    /// </summary>
    public static void ConfirmAndKill(int pid, string repo, string shortId, string displayName)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }
        dispatcher.BeginInvoke(() => Confirm(pid, repo, shortId, displayName), DispatcherPriority.ApplicationIdle);
    }

    private static void Confirm(int pid, string repo, string shortId, string displayName)
    {
        var text = $"Kill session {shortId} ({displayName}) in {repo}?{Environment.NewLine}{Environment.NewLine}"
                 + $"This ends claude.exe (PID {pid}) and every process it started. Unsaved work in that session is lost.";
        var owner = Application.Current?.MainWindow;
        var answer = owner is { IsVisible: true }
            ? MessageBox.Show(owner, text, "Kill session", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(text, "Kill session", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        Log.Information("Kill session {Repo}/{Session} (pid {Pid}): confirmation answered {Answer}", repo, shortId, pid, answer);
        if (answer == MessageBoxResult.Yes)
        {
            Kill(pid, repo, shortId);
        }
    }

    public static bool Kill(int pid, string repo, string shortId)
    {
        try
        {
            if (pid <= 0 || pid == Environment.ProcessId)
            {
                Log.Warning("Kill session {Repo}/{Session}: refused pid {Pid}", repo, shortId, pid);
                return false;
            }

            using var p = Process.GetProcessById(pid);
            if (!string.Equals(p.ProcessName, "claude", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Kill session {Repo}/{Session}: pid {Pid} is {Name}, not claude — refused",
                    repo, shortId, pid, p.ProcessName);
                return false;
            }

            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            Log.Information("Killed session {Repo}/{Session}: claude.exe pid {Pid} and its process tree (exited={Exited})",
                repo, shortId, pid, p.HasExited);
            return p.HasExited;
        }
        catch (ArgumentException)
        {
            Log.Information("Kill session {Repo}/{Session}: pid {Pid} already gone", repo, shortId, pid);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Kill session {Repo}/{Session}: pid {Pid} failed", repo, shortId, pid);
            return false;
        }
    }
}
