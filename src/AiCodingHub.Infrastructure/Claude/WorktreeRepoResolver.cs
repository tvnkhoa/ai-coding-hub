using System;
using System.IO;
using System.Text.RegularExpressions;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Names the repo of a session missing from the handover registry the way the working-day hook
/// does: from git's common dir, so a linked worktree joins its main repo's group.
/// <para>
/// Cwd present: walk up to the first <c>.git</c>. A directory names its own repo; a file
/// (<c>gitdir: &lt;main&gt;/.git/worktrees/&lt;x&gt;</c>) names the main repo (via the worktree's
/// <c>commondir</c> when present). Cwd gone (worktree removed): a folder <c>&lt;repo&gt;-wt-&lt;x&gt;</c>
/// on branch <c>wt/&lt;x&gt;</c> names <c>&lt;repo&gt;</c>. Otherwise null.
/// </para>
/// Touches only the file system under the cwd; never throws.
/// </summary>
public static class WorktreeRepoResolver
{
    private static readonly Regex WorktreeFolder = new(@"^(?<repo>.+)-wt-(?<name>.+)$", RegexOptions.CultureInvariant);

    /// <summary>The repo name and whether <paramref name="cwd"/> is a linked worktree of it, or null.</summary>
    public static (string Repo, bool IsWorktree)? Resolve(string? cwd, string? branch)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        try
        {
            var full = NormalizePath(cwd);
            // A disconnected share can block Directory.Exists for seconds: name it from the folder only.
            if (!IsNetworkPath(full) && Directory.Exists(full))
            {
                return FromGit(full);
            }

            var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var m = WorktreeFolder.Match(leaf);
            if (m.Success && string.Equals(branch, "wt/" + m.Groups["name"].Value, StringComparison.OrdinalIgnoreCase))
            {
                return (m.Groups["repo"].Value, true);
            }
        }
        catch (Exception)
        {
            // unreadable path: caller falls back to the folder name
        }

        return null;
    }

    /// <summary>
    /// Full path without a <c>\\?\</c> prefix (<c>\\?\UNC\s\x</c> → <c>\\s\x</c>), backslashes, no
    /// trailing separator. 8.3 names, subst drives and junctions are not resolved. Never throws.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            p = @"\\" + p[8..];
        }
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p[4..];
        }

        try
        {
            p = Path.GetFullPath(p);
        }
        catch (Exception)
        {
            // keep as is
        }

        var root = Path.GetPathRoot(p) ?? "";
        return p.Length > root.Length ? p.TrimEnd('\\') : p;
    }

    /// <summary>
    /// True for a UNC path or a drive mapped to a network share: the file system is not probed
    /// there on the poll. Never throws.
    /// </summary>
    public static bool IsNetworkPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return fullPath.Length >= 2 && fullPath[1] == ':'
                && new DriveInfo(fullPath[..1]).DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static (string Repo, bool IsWorktree)? FromGit(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var dotGit = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(dotGit))
            {
                return (dir.Name, false);
            }

            if (!File.Exists(dotGit))
            {
                continue;
            }

            var line = File.ReadAllText(dotGit).Trim();
            if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var gitDir = line["gitdir:".Length..].Trim();
            if (!Path.IsPathRooted(gitDir))
            {
                gitDir = Path.Combine(dir.FullName, gitDir);
            }
            gitDir = Path.GetFullPath(gitDir);

            var common = CommonDir(gitDir);
            if (common is null)
            {
                return null;
            }

            var main = Path.GetDirectoryName(common.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(main) ? null : (Path.GetFileName(main), true);
        }

        return null;
    }

    /// <summary><c>&lt;gitdir&gt;/commondir</c> when present, else the parent of <c>…/.git/worktrees/&lt;x&gt;</c>.</summary>
    private static string? CommonDir(string gitDir)
    {
        var commonFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonFile))
        {
            var rel = File.ReadAllText(commonFile).Trim();
            if (rel.Length > 0)
            {
                return Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(gitDir, rel));
            }
        }

        var worktrees = Path.GetDirectoryName(gitDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return worktrees is not null && string.Equals(Path.GetFileName(worktrees), "worktrees", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(worktrees)
            : null;
    }
}
