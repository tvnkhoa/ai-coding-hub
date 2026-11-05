using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

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
            var full = CanonicalPath(cwd);
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
    /// trailing separator. String only: aliases are resolved by <see cref="CanonicalPath"/>. Never throws.
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
    /// <see cref="NormalizePath"/>, then the same directory under its one real name: 8.3 short
    /// names expanded, subst drives, junctions and symlinks followed to their target. A network
    /// path is never probed; a path that cannot be opened falls back to its long name, then to
    /// the string normalisation. Resolved paths are cached per input (bounded). Never throws.
    /// </summary>
    public static string CanonicalPath(string path)
    {
        if (Canonical.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var normal = NormalizePath(path);
        if (IsNetworkPath(normal))
        {
            return normal;
        }

        string? resolved = null;
        try
        {
            resolved = FinalPath(normal) ?? LongPath(normal);
        }
        catch (Exception)
        {
            // fall back to the string form
        }

        if (resolved is null)
        {
            return normal; // not cached: the directory may appear later
        }

        resolved = NormalizePath(resolved);
        if (Canonical.Count >= CanonicalCacheLimit)
        {
            Canonical.Clear();
        }
        Canonical[path] = resolved;
        return resolved;
    }

    private const int CanonicalCacheLimit = 512;
    private static readonly ConcurrentDictionary<string, string> Canonical = new(StringComparer.Ordinal);

    /// <summary>Final path of an existing file or directory, following subst, junctions and symlinks.</summary>
    private static string? FinalPath(string path)
    {
        using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }
        return length == 0 || length >= buffer.Capacity ? null : buffer.ToString(0, (int)length);
    }

    /// <summary>Long-name form of an existing path (8.3 expanded), or null.</summary>
    private static string? LongPath(string path)
    {
        var buffer = new StringBuilder(512);
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        }
        return length == 0 || length >= buffer.Capacity ? null : buffer.ToString(0, (int)length);
    }

    private const uint FileShareAll = 0x7; // read | write | delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000; // needed to open a directory

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint length);

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
