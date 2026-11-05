using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using AiCodingHub.Core.Pricing;
using AiCodingHub.Infrastructure.System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Monitors every relevant Claude Code session at once and groups them by repo with their
/// working-day role (leader / member). Each session keeps its own <see cref="SessionTracker"/>,
/// so a poll reads only appended bytes and nothing is re-read when the owner switches windows.
/// <para>
/// Which sessions: today's sessions in the handover registry (while their process runs, plus
/// <c>EndedHideMinutes</c> after it exits) and any other transcript written within
/// <c>ActiveWindowMinutes</c>. <c>MaxSessions</c> caps only sessions without a running process;
/// a live one is never cut. The file system is re-scanned every
/// <see cref="DiscoveryInterval"/>; tracked files are tailed on every poll.
/// </para>
/// Called from a single background loop, so instance state is not synchronized. Never throws.
/// </summary>
public sealed class ClaudeProvider : IClaudeProvider
{
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(2);

    /// <summary>Bytes of backlog folded per session per poll (keeps each poll short).</summary>
    private const long PumpBudgetBytes = 16L << 20;

    private readonly IOptionsMonitor<HudOptions> _options;
    private readonly IPricingCatalog _pricing;
    private readonly TimeProvider _time;
    private readonly IProcessInspector _processes;
    private readonly ILogger<ClaudeProvider> _log;
    private readonly ClaudeSettingsReader _settings;

    private readonly TranscriptParser _parser = new();
    private readonly Dictionary<string, SessionTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);

    private HandoverRegistryReader? _registry;
    private string? _registryDir;
    private IReadOnlyDictionary<string, RegisteredSession> _registered = new Dictionary<string, RegisteredSession>();
    private Dictionary<int, bool> _alive = new();
    private DecisionMarkerReader? _markers;
    private string? _markerDir;
    private IReadOnlyDictionary<int, int> _parents = new Dictionary<int, int>();
    private IReadOnlyList<ClaudeProcess>? _claudeProcesses;
    private readonly Dictionary<string, (string Repo, bool IsWorktree)?> _repoByCwd = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;

    private static readonly Regex ClaimsSession = new(
        @"(?:--session-id|--resume|-r)[\s=]+""?(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ClaudeProvider(
        IOptionsMonitor<HudOptions> options,
        IPricingCatalog pricing,
        TimeProvider time,
        IProcessInspector processes,
        ILogger<ClaudeProvider> log,
        ClaudeSettingsReader? settings = null)
    {
        _options = options;
        _pricing = pricing;
        _time = time;
        _processes = processes;
        _log = log;
        _settings = settings ?? new ClaudeSettingsReader();
    }

    public Task<ClaudeSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var now = _time.GetUtcNow();
            var opts = _options.CurrentValue;

            if (now - _lastDiscovery >= DiscoveryInterval)
            {
                Discover(opts, now);
                _lastDiscovery = now;
            }

            foreach (var tracker in _trackers.Values)
            {
                try
                {
                    tracker.Pump(PumpBudgetBytes);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Tailing {Path} failed", tracker.File.Path);
                }
            }

            return Task.FromResult(Build(opts, now));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Claude provider failed; returning empty slice");
            return Task.FromResult(ClaudeSnapshot.Empty);
        }
    }

    private void Discover(HudOptions opts, DateTimeOffset now)
    {
        var root = string.IsNullOrWhiteSpace(opts.ClaudeProjectsRoot) ? SessionLocator.DefaultRoot : opts.ClaudeProjectsRoot!;
        if (_registry is null || !string.Equals(_registryDir, opts.HandoverDir, StringComparison.OrdinalIgnoreCase))
        {
            _registryDir = opts.HandoverDir;
            _registry = new HandoverRegistryReader(opts.HandoverDir);
        }

        _registered = _registry.ReadToday(now);

        // One process pass per poll: parent links, liveness and the claude.exe list (PEB reads cached).
        var processes = _processes.Snapshot();
        _alive = _registered.Values
            .Where(r => r.Pid is not null)
            .Select(r => r.Pid!.Value)
            .Distinct()
            .ToDictionary(pid => pid, pid => processes.IsAlive(pid, _processes.IsAlive));
        _parents = processes.Parents;

        var activeWindow = TimeSpan.FromMinutes(Math.Max(1, opts.ActiveWindowMinutes));
        var endedHide = TimeSpan.FromMinutes(Math.Max(0, opts.EndedHideMinutes));
        var nowUtc = now.UtcDateTime;

        // Sessions missing from the registry have no pid: match them to running claude.exe instead.
        _claudeProcesses = processes.Claude;

        var candidates = SessionLocator.EnumerateSessions(root)
            .Where(f =>
            {
                var age = nowUtc - f.LastWriteUtc;
                if (_registered.TryGetValue(f.SessionId, out var reg))
                {
                    var alive = reg.Pid is { } pid ? _alive.GetValueOrDefault(pid, true) : age <= activeWindow;
                    // EndedHideMinutes 0 (default): a session leaves the HUD as soon as its process exits.
                    return alive || (endedHide > TimeSpan.Zero && age <= endedHide);
                }
                return age <= activeWindow;
            })
            .OrderByDescending(f => f.LastWriteUtc)
            .ToList();

        // A session whose process runs is never cut; MaxSessions caps the rest (ended, or recent
        // without a known process), newest first, in the slots the live ones leave.
        var byLive = candidates.ToLookup(IsLive);
        var live = byLive[true].ToList();
        var wanted = live
            .Concat(byLive[false].Take(Math.Max(0, Math.Max(1, opts.MaxSessions) - live.Count)))
            .ToList();

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in wanted)
        {
            keep.Add(file.Path);
            if (_trackers.TryGetValue(file.Path, out var tracker))
            {
                tracker.File = file;
            }
            else
            {
                _trackers[file.Path] = new SessionTracker(file, _parser, _pricing, PumpBudgetBytes);
            }
        }

        foreach (var gone in _trackers.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _trackers.Remove(gone);
        }
    }

    /// <summary>
    /// The session's process runs: its registered pid is alive, or (unregistered) a claude.exe
    /// names its id, or a bare claude.exe runs in the cwd its project folder encodes. Cheap: no
    /// transcript parse; used only to exempt live sessions from <c>MaxSessions</c>.
    /// </summary>
    private bool IsLive(SessionFile file)
    {
        if (_registered.TryGetValue(file.SessionId, out var reg))
        {
            return reg.Pid is { } pid && _alive.GetValueOrDefault(pid, true);
        }

        if (_claudeProcesses is null)
        {
            return false;
        }

        foreach (var p in _claudeProcesses)
        {
            if (p.CommandLine is { } cl && ClaimsSession.Match(cl) is { Success: true } claim)
            {
                if (string.Equals(claim.Groups["id"].Value, file.SessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (!string.IsNullOrWhiteSpace(p.Cwd)
                && string.Equals(EncodeProjectDir(WorktreeRepoResolver.NormalizePath(p.Cwd)), file.ProjectDir,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Claude Code's project folder name for a cwd: every non-alphanumeric char becomes '-'.</summary>
    private static string EncodeProjectDir(string cwd)
    {
        var chars = cwd.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]))
            {
                chars[i] = '-';
            }
        }
        return new string(chars);
    }

    /// <summary>
    /// A prompt open right now according to the hook marker (Claude Code writes the question to
    /// the transcript only once it is answered). Ignored when the transcript already has the answer.
    /// </summary>
    private static DecisionRequest? FromMarker(Dictionary<string, DecisionMarker> markers, SessionTracker tracker)
    {
        if (!markers.TryGetValue(tracker.File.SessionId, out var m)
            || (m.ToolUseId is { } id && tracker.IsAnswered(id)))
        {
            return null;
        }

        return m.Tool == "ExitPlanMode"
            ? new DecisionRequest(m.ToolUseId ?? "plan", "plan", "Plan", "Plan ready for approval", m.At)
            : new DecisionRequest(m.ToolUseId ?? "question", "question", m.Header ?? "Question", m.Question ?? "", m.At);
    }

    private ClaudeSnapshot Build(HudOptions opts, DateTimeOffset now)
    {
        if (_trackers.Count == 0)
        {
            return ClaudeSnapshot.Empty;
        }

        var idle = TimeSpan.FromMinutes(Math.Max(1, opts.IdleMinutes));
        var endedHide = TimeSpan.FromMinutes(Math.Max(0, opts.EndedHideMinutes));
        var foreground = _processes.ForegroundProcessId();
        if (_markers is null || !string.Equals(_markerDir, opts.DecisionMarkerDir, StringComparison.OrdinalIgnoreCase))
        {
            _markerDir = opts.DecisionMarkerDir;
            _markers = new DecisionMarkerReader(opts.DecisionMarkerDir);
        }
        var markers = _markers.Read(now);

        var rows = new List<(SessionSummary Summary, ClaudeSnapshot Snapshot)>();
        foreach (var tracker in _trackers.Values)
        {
            var snap = tracker.Snapshot(idle, now);
            if (!snap.HasActiveSession)
            {
                continue; // nothing parsed yet (brand-new, empty file)
            }

            snap = snap with { Context = snap.Context with { MaxTokens = ResolveWindow(snap.Session.Model, snap.Context) } };
            _registered.TryGetValue(tracker.File.SessionId, out var reg);

            var lastActivity = snap.Task.LastActivityAt
                ?? new DateTimeOffset(tracker.File.LastWriteUtc, TimeSpan.Zero);
            var lastWrite = new DateTimeOffset(tracker.File.LastWriteUtc, TimeSpan.Zero);
            if (lastWrite > lastActivity)
            {
                lastActivity = lastWrite; // tool output lines may lack timestamps
            }

            bool ended;
            int? pid;
            var role = reg?.Role ?? SessionRole.None;
            string group;
            if (reg is not null)
            {
                pid = reg.Pid;
                ended = reg.Pid is { } p && !_alive.GetValueOrDefault(p, true);
                group = reg.Repo;
            }
            else
            {
                var (alive, matched) = MatchProcess(_claudeProcesses, tracker.File.SessionId, snap.Session.Workspace,
                    snap.Session.StartedAt, TrackedLastWrite, snap.Session.CurrentCwd);
                pid = matched;
                ended = alive == false;
                if (ended && !(endedHide > TimeSpan.Zero && now - lastWrite <= endedHide))
                {
                    continue; // its process exited: hidden like a registered dead session
                }

                var repo = ResolveRepo(snap.Session.Workspace, snap.Session.Branch);
                group = repo?.Repo
                    ?? (snap.Session.ProjectName is { Length: > 0 } pn && pn != "—" ? pn : tracker.File.ProjectDir);
                if (repo is { IsWorktree: true })
                {
                    role = SessionRole.Member; // a worktree session joins its main repo, never as leader
                }
            }

            var activity = ended ? SessionActivity.Ended
                : now - lastActivity > idle ? SessionActivity.Idle
                : SessionActivity.Active;

            var model = snap.Session.Model;

            var summary = new SessionSummary(
                SessionId: tracker.File.SessionId,
                Group: group,
                Role: role,
                Model: model,
                ModelLabel: model == "—" ? "—" : _pricing.Resolve(model).Label,
                ProjectName: snap.Session.ProjectName,
                Workspace: snap.Session.Workspace,
                ContextTokens: snap.Context.CurrentTokens,
                MaxTokens: snap.Context.MaxTokens,
                SessionCostUsd: snap.Cost.SessionUsd,
                Requests: snap.Requests.Requests,
                StartedAt: snap.Session.StartedAt,
                LastActivityAt: lastActivity,
                Pid: pid,
                IsFocused: false,
                Activity: activity,
                Name: snap.Name,
                // A session whose process exited is not waiting on anyone any more.
                Decision: activity == SessionActivity.Ended ? null
                    : snap.PendingDecision ?? FromMarker(markers, tracker));

            rows.Add((summary, snap));
        }

        if (rows.Count == 0)
        {
            return ClaudeSnapshot.Empty;
        }

        MarkFocused(rows, foreground);

        var groups = rows
            .Select(r => r.Summary)
            .GroupBy(s => s.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SessionGroup(
                g.Key,
                g.OrderBy(s => s.Role == SessionRole.Leader ? 0 : s.Role == SessionRole.Member ? 1 : 2)
                 .ThenBy(s => s.StartedAt ?? DateTimeOffset.MaxValue)
                 .ThenBy(s => s.SessionId, StringComparer.Ordinal)
                 .ToList()))
            // Registry repos first (stable alphabetical order), then ad-hoc sessions.
            .OrderBy(g => g.Sessions.Any(s => s.Role != SessionRole.None) ? 0 : 1)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Primary session for single-session consumers: focused, else most recently active live one.
        var primary = rows.FirstOrDefault(r => r.Summary.IsFocused);
        if (primary.Snapshot is null)
        {
            primary = rows
                .OrderBy(r => r.Summary.Activity == SessionActivity.Ended ? 1 : 0)
                .ThenByDescending(r => r.Summary.LastActivityAt)
                .First();
        }

        return primary.Snapshot with { Groups = groups };
    }

    /// <summary>Repo of an unregistered session's cwd, cached (the cwd of a removed worktree keeps its answer).</summary>
    private (string Repo, bool IsWorktree)? ResolveRepo(string workspace, string branch)
    {
        var key = workspace + "|" + branch;
        if (!_repoByCwd.TryGetValue(key, out var repo))
        {
            repo = WorktreeRepoResolver.Resolve(workspace, branch == "—" ? null : branch);
            if (_repoByCwd.Count > 512)
            {
                _repoByCwd.Clear();
            }
            _repoByCwd[key] = repo;
        }
        return repo;
    }

    /// <summary>
    /// Liveness of a session missing from the registry: a claude.exe whose command line names the
    /// session id, else one that names no session (a bare <c>claude</c>) running in the same cwd
    /// (the transcript's first or <paramref name="currentCwd"/>, its latest).
    /// Last, a same-cwd process naming another session X that X no longer accounts for (X is not
    /// tracked, or its transcript was last written before this session started): <c>claude --resume X</c>
    /// then <c>/clear</c> keeps X on the command line while writing the new session.
    /// Alive null = cannot tell (enumeration failed, or a process could not be read), so the
    /// transcript-age rule stands. Pid is set when exactly one process matches.
    /// </summary>
    internal static (bool? Alive, int? Pid) MatchProcess(
        IReadOnlyList<ClaudeProcess>? processes,
        string sessionId,
        string? cwd,
        DateTimeOffset? startedAt = null,
        Func<string, DateTimeOffset?>? trackedLastWrite = null,
        string? currentCwd = null)
    {
        if (processes is null)
        {
            return (null, null);
        }

        foreach (var p in processes)
        {
            if (p.CommandLine?.Contains(sessionId, StringComparison.OrdinalIgnoreCase) == true)
            {
                return (true, p.Pid);
            }
        }

        var byCwd = new List<int>();
        var handedOver = new List<int>();
        var unreadable = false;
        foreach (var p in processes)
        {
            if (p.CommandLine is null && p.Cwd is null)
            {
                unreadable = true;
                continue;
            }

            // First cwd or latest: EnterWorktree chdirs claude.exe into the worktree, and the PEB
            // cwd is cached, so the process may report either.
            if (!SamePath(p.Cwd, cwd) && !SamePath(p.Cwd, currentCwd))
            {
                continue;
            }

            if (p.CommandLine is { } cl && ClaimsSession.Match(cl) is { Success: true } claim)
            {
                // Bound to another session, unless that session stopped before this one began.
                var other = trackedLastWrite?.Invoke(claim.Groups["id"].Value);
                if (other is null || (startedAt is { } start && other < start))
                {
                    handedOver.Add(p.Pid);
                }
                continue;
            }

            byCwd.Add(p.Pid);
        }

        if (byCwd.Count == 0)
        {
            byCwd = handedOver;
        }

        if (byCwd.Count > 0)
        {
            return (true, byCwd.Count == 1 ? byCwd[0] : null);
        }
        return unreadable ? (null, null) : (false, null);
    }

    /// <summary>Last write of a tracked transcript by session id, or null when it is not tracked.</summary>
    private DateTimeOffset? TrackedLastWrite(string sessionId)
    {
        foreach (var t in _trackers.Values)
        {
            if (string.Equals(t.File.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return new DateTimeOffset(t.File.LastWriteUtc, TimeSpan.Zero);
            }
        }
        return null;
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        // Canonical: 8.3 short names, subst drives and junctions name the same directory.
        return string.Equals(WorktreeRepoResolver.CanonicalPath(a), WorktreeRepoResolver.CanonicalPath(b),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Marks the session whose claude.exe descends from the foreground window's process. Several
    /// matches (tabs of one terminal window) are ambiguous, so none is marked.
    /// </summary>
    private void MarkFocused(List<(SessionSummary Summary, ClaudeSnapshot Snapshot)> rows, int? foreground)
    {
        if (foreground is not { } fg)
        {
            return;
        }

        var matches = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Summary is { Pid: { } pid, Activity: not SessionActivity.Ended }
                && ProcessInspector.IsAncestor(_parents, fg, pid))
            {
                matches.Add(i);
            }
        }

        if (matches.Count == 1)
        {
            var i = matches[0];
            rows[i] = (rows[i].Summary with { IsFocused = true }, rows[i].Snapshot);
        }
    }

    /// <summary>
    /// Context window for one session: the model's native window, widened to 1M when the user's
    /// settings select the 1M beta for that model family, or when the context already exceeds the
    /// native window (which proves the larger window is in use).
    /// </summary>
    private long ResolveWindow(string model, ContextInfo context)
    {
        var window = context.MaxTokens > 0 ? context.MaxTokens : ModelCatalog.DefaultWindow;
        if (window < ModelCatalog.LargeWindow
            && (context.CurrentTokens > window || _settings.IsLargeContextFor(model)))
        {
            window = ModelCatalog.LargeWindow;
        }
        return window;
    }
}
