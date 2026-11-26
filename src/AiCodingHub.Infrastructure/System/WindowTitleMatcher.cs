using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AiCodingHub.Infrastructure.System;

/// <summary>
/// Picks which of one process's top-level windows hosts a session. Every VS Code window belongs
/// to one <c>Code.exe</c>, so the window is told apart by its title, which names the open folder:
/// <c>"file.cs — ai-coding-hub — Visual Studio Code"</c> (also <c>" - "</c> separators,
/// <c>"● "</c> dirty marker, <c>"name (Workspace)"</c>). Pure; no I/O.
/// </summary>
public static class WindowTitleMatcher
{
    private static readonly string[] Separators = [" — ", " – ", " - "];

    /// <summary>
    /// The folder names a session's window title may carry, most specific first: the leaf of the
    /// session's cwd, then its repo (group) name — a worktree session's window may be opened on
    /// the main checkout.
    /// </summary>
    public static IReadOnlyList<string> FolderHints(string? workspace, string? repo)
    {
        var hints = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var leaf = Path.GetFileName(workspace.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(leaf)) hints.Add(leaf);
        }
        if (!string.IsNullOrWhiteSpace(repo) && !hints.Contains(repo, StringComparer.OrdinalIgnoreCase))
        {
            hints.Add(repo);
        }
        return hints;
    }

    /// <summary>
    /// Index of the title that names a hint as a whole title segment (hints in order, titles in
    /// z-order), else the first title containing a hint, else 0. -1 only when there are no titles.
    /// </summary>
    public static int Pick(IReadOnlyList<string> titles, IReadOnlyList<string> hints)
    {
        if (titles.Count == 0) return -1;

        foreach (var hint in hints)
        {
            for (var i = 0; i < titles.Count; i++)
            {
                if (HasSegment(titles[i], hint)) return i;
            }
        }

        foreach (var hint in hints)
        {
            for (var i = 0; i < titles.Count; i++)
            {
                if (titles[i].Contains(hint, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }

        return 0;
    }

    private static bool HasSegment(string title, string hint)
    {
        foreach (var raw in title.Split(Separators, StringSplitOptions.None))
        {
            var seg = raw.Trim().TrimStart('●').Trim();
            if (seg.StartsWith('[') && seg.IndexOf(']') is var close and > 0)
            {
                seg = seg[(close + 1)..].Trim(); // "[Administrator] name"
            }
            if (string.Equals(seg, hint, StringComparison.OrdinalIgnoreCase)
                || seg.StartsWith(hint + " (", StringComparison.OrdinalIgnoreCase)) // "name (Workspace)"
            {
                return true;
            }
        }
        return false;
    }
}
