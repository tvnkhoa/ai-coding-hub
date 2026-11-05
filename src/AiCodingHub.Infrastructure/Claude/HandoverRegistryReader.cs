using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCodingHub.Core.Models;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>One session registered by the working-day hook.</summary>
public sealed record RegisteredSession(string SessionId, string Repo, string Day, DateTimeOffset At, int? Pid, SessionRole Role);

/// <summary>
/// Reads the owner's working-day handover data (written by <c>~/.claude/hooks/handover.mjs</c>),
/// read-only:
/// <list type="bullet">
/// <item><c>.sessions.jsonl</c> — one line per session start/resume:
/// <c>{session, repo, day, source, at, pid, ppid}</c>; <c>pid</c> is the claude.exe PID.</item>
/// <item><c>.coordinator-&lt;day&gt;-&lt;repo&gt;</c> — holds the session id of that repo's leader.</item>
/// </list>
/// The working day runs 04:00 → 04:00 local. Missing files yield an empty result; nothing throws.
/// </summary>
public sealed class HandoverRegistryReader
{
    /// <summary>Hour the working day starts (the hook's rule).</summary>
    public const int WorkingDayStartHour = 4;

    private readonly string _dir;
    private DateTime _registryWrite = DateTime.MinValue;
    private long _registryLength = -1;
    private List<RawEntry> _entries = new();

    private sealed record RawEntry(string SessionId, string Repo, string Day, DateTimeOffset At, int? Pid);

    public HandoverRegistryReader(string? dir = null) => _dir = string.IsNullOrWhiteSpace(dir) ? DefaultDir : dir;

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "handover");

    /// <summary>Working-day name (<c>yyyy-MM-dd</c>) for a moment, using the 04:00 boundary.</summary>
    public static string WorkingDay(DateTimeOffset now) =>
        now.ToLocalTime().AddHours(-WorkingDayStartHour).ToString("yyyy-MM-dd");

    /// <summary>Latest entry per session registered for today's working day, with its role.</summary>
    public IReadOnlyDictionary<string, RegisteredSession> ReadToday(DateTimeOffset now)
    {
        var result = new Dictionary<string, RegisteredSession>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Reload();
            var day = WorkingDay(now);
            var leaders = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in _entries)
            {
                if (!string.Equals(e.Day, day, StringComparison.Ordinal))
                {
                    continue;
                }

                if (result.TryGetValue(e.SessionId, out var existing) && existing.At > e.At)
                {
                    continue; // keep the latest (resume/compact gives a new pid)
                }

                if (!leaders.TryGetValue(e.Repo, out var leader))
                {
                    leader = ReadLeader(day, e.Repo);
                    leaders[e.Repo] = leader;
                }

                var role = string.Equals(leader, e.SessionId, StringComparison.OrdinalIgnoreCase)
                    ? SessionRole.Leader
                    : SessionRole.Member;
                result[e.SessionId] = new RegisteredSession(e.SessionId, e.Repo, e.Day, e.At, e.Pid, role);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    private void Reload()
    {
        var path = Path.Combine(_dir, ".sessions.jsonl");
        var trimming = TrimmingFiles();
        var exists = File.Exists(path);
        if (!exists && trimming.Count == 0)
        {
            _entries = new List<RawEntry>();
            _registryWrite = DateTime.MinValue;
            _registryLength = -1;
            return;
        }

        // While a hook trim runs, old rows sit in a `.trimming` side file: never serve the cache then.
        FileInfo? info = exists ? new FileInfo(path) : null;
        if (trimming.Count == 0 && info is not null
            && info.LastWriteTimeUtc == _registryWrite && info.Length == _registryLength)
        {
            return;
        }

        // The hook's own order: registry, the .trimming files, registry again — a trim renames, appends
        // the kept rows back, then deletes the side file, so every row is in one of the three reads.
        var entries = new List<RawEntry>();
        ReadInto(path, entries);
        foreach (var t in trimming)
        {
            ReadInto(t, entries);
        }

        ReadInto(path, entries);

        _entries = entries;
        if (trimming.Count == 0 && info is not null)
        {
            _registryWrite = info.LastWriteTimeUtc;
            _registryLength = info.Length;
        }
        else
        {
            _registryWrite = DateTime.MinValue;
            _registryLength = -1;
        }
    }

    private List<string> TrimmingFiles()
    {
        try
        {
            return Directory.GetFiles(_dir, ".sessions.jsonl.*.trimming")
                .Where(f => TrimmingName.IsMatch(Path.GetFileName(f)))
                .ToList();
        }
        catch (IOException)
        {
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>Appends the parsed rows of a file; a file that vanished or is locked mid-read adds nothing.</summary>
    private static void ReadInto(string path, List<RawEntry> entries)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (TryParse(line) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static readonly Regex TrimmingName = new(@"^\.sessions\.jsonl\.\d+\.trimming$", RegexOptions.Compiled);

    private string? ReadLeader(string day, string repo)
    {
        var path = Path.Combine(_dir, $".coordinator-{day}-{repo}");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static RawEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var session = Str(root, "session");
            var repo = Str(root, "repo");
            var day = Str(root, "day");
            if (session is null || repo is null || day is null)
            {
                return null;
            }

            var at = DateTimeOffset.TryParse(Str(root, "at"), out var ts) ? ts : DateTimeOffset.MinValue;
            int? pid = root.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) && n > 0
                ? n
                : null;
            return new RawEntry(session, repo, day, at, pid);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
