using System;
using System.Collections.Generic;
using System.IO;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Pricing;
using TaskStatus = AiCodingHub.Core.Models.TaskStatus;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Incremental fold of one session's transcript events. Free of I/O and wall-clock (time is
/// passed into <see cref="ToSnapshot"/>), so it stays testable against hand-authored fixtures.
/// <para>
/// Claude Code writes one line per content block of an assistant message and repeats the same
/// <c>usage</c> on each, so usage, cost and the request count are taken once per response key
/// (<c>message.id</c> + <c>requestId</c>); tool calls are still summed across the lines.
/// </para>
/// </summary>
public sealed class SessionAccumulator
{
    private readonly IPricingCatalog _pricing;
    private readonly HashSet<string> _seenResponses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PendingQuestion Question, DateTimeOffset? At)> _pending = new(StringComparer.Ordinal);
    private string? _name;

    private string? _model, _cwd, _branch, _version;
    private DateTimeOffset? _first, _last;
    private UsageTokens? _lastAssistantUsage;
    private TranscriptEvent? _lastEvent;
    private decimal _sessionCost;
    private int _requests, _prompts, _toolCalls, _assistantWithUsage;
    private long _outputTokenSum;
    private double _latencySum, _latencyMax;
    private int _latencyCount;
    private DateTimeOffset? _pendingUserTs;

    public SessionAccumulator(IPricingCatalog pricing) => _pricing = pricing;

    public int EventCount { get; private set; }

    public string? Model => _model;

    public void Add(TranscriptEvent ev)
    {
        EventCount++;
        _lastEvent = ev;

        if (ev.Timestamp is { } ts)
        {
            _first ??= ts;
            _last = ts;
        }

        if (!string.IsNullOrWhiteSpace(ev.SessionName)) _name = ev.SessionName;
        if (ev.AnsweredToolUseIds is { } answered)
        {
            foreach (var id in answered)
            {
                _pending.Remove(id);
            }
        }
        if (ev.Role == EventRole.Assistant && ev.Question is { } question)
        {
            _pending[question.ToolUseId] = (question, ev.Timestamp);
        }

        if (ev.Cwd is not null) _cwd = ev.Cwd;
        if (ev.GitBranch is not null) _branch = ev.GitBranch;
        if (ev.Version is not null) _version = ev.Version;

        switch (ev.Role)
        {
            case EventRole.User:
                if (!ev.IsToolResult)
                {
                    _prompts++;
                    _pendingUserTs = ev.Timestamp ?? _pendingUserTs;
                }
                break;

            case EventRole.Assistant:
                _toolCalls += ev.ToolUseCount;
                if (ev.Model is not null) _model = ev.Model;

                // A repeated line of an already-counted response adds nothing but tool calls.
                var key = ev.ResponseKey;
                if (key is not null && !_seenResponses.Add(key))
                {
                    break;
                }

                _requests++;
                if (ev.Usage is { } u)
                {
                    _lastAssistantUsage = u;
                    _outputTokenSum += u.Output;
                    _assistantWithUsage++;
                    _sessionCost += TokenCostCalculator.Cost(u, _pricing.Resolve(ev.Model ?? _model));
                }

                if (_pendingUserTs is { } startTs && ev.Timestamp is { } endTs && endTs >= startTs)
                {
                    var s = (endTs - startTs).TotalSeconds;
                    _latencySum += s;
                    _latencyCount++;
                    if (s > _latencyMax) _latencyMax = s;
                    _pendingUserTs = null;
                }
                break;
        }
    }

    public void AddRange(IEnumerable<TranscriptEvent> events)
    {
        foreach (var ev in events)
        {
            Add(ev);
        }
    }

    public ClaudeSnapshot ToSnapshot(TimeSpan idleThreshold, DateTimeOffset now)
    {
        if (_lastEvent is null)
        {
            return ClaudeSnapshot.Empty;
        }

        var pricing = _pricing.Resolve(_model);
        var contextTokens = _lastAssistantUsage?.ContextTokens ?? 0;
        var context = new ContextInfo(contextTokens, pricing.ContextWindow);

        var duration = _first is { } f && _last is { } l && l >= f ? l - f : TimeSpan.Zero;
        var workspace = _cwd ?? "";
        var project = string.IsNullOrEmpty(workspace) ? "—" : SafeLeaf(workspace);

        var session = new SessionInfo(
            Model: string.IsNullOrEmpty(_model) ? "—" : _model!,
            Workspace: workspace,
            ProjectName: project,
            ClaudeVersion: _version ?? "—",
            StartedAt: _first,
            Duration: duration,
            Branch: string.IsNullOrEmpty(_branch) ? "—" : _branch!);

        var avgLatency = _latencyCount > 0 ? _latencySum / _latencyCount : 0;
        var avgTokens = _assistantWithUsage > 0 ? (double)_outputTokenSum / _assistantWithUsage : 0;
        var stats = new RequestStats(_requests, _prompts, _toolCalls, avgLatency, _latencyMax, avgTokens);

        DecisionRequest? decision = null;
        foreach (var (q, at) in _pending.Values)
        {
            // The oldest unanswered question is the one the session is blocked on.
            if (decision is null || (at ?? DateTimeOffset.MaxValue) < (decision.Since ?? DateTimeOffset.MaxValue))
            {
                decision = new DecisionRequest(q.ToolUseId, q.Kind, q.Header, q.Question, at);
            }
        }

        return new ClaudeSnapshot(
            HasActiveSession: true,
            Session: session,
            Context: context,
            Cost: new CostInfo(_sessionCost, 0m, 0m, 0m),
            Requests: stats,
            Task: TranscriptDeriver.DeriveTask(_lastEvent, _last, now, idleThreshold))
        {
            Name = _name,
            PendingDecision = decision,
        };
    }

    private static string SafeLeaf(string path)
    {
        try
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leaf = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
        }
        catch
        {
            return path;
        }
    }
}

/// <summary>
/// Pure derivation of a <see cref="ClaudeSnapshot"/> from an ordered event list — a one-shot
/// <see cref="SessionAccumulator"/>. Cost buckets beyond the session (today/week/month) come
/// from the cost ledger.
/// </summary>
public static class TranscriptDeriver
{
    public static ClaudeSnapshot Derive(
        IReadOnlyList<TranscriptEvent> events,
        IPricingCatalog pricing,
        TimeSpan idleThreshold,
        DateTimeOffset now)
    {
        if (events.Count == 0)
        {
            return ClaudeSnapshot.Empty;
        }

        var acc = new SessionAccumulator(pricing);
        acc.AddRange(events);
        return acc.ToSnapshot(idleThreshold, now);
    }

    internal static TaskStatus DeriveTask(
        TranscriptEvent last,
        DateTimeOffset? lastActivity,
        DateTimeOffset now,
        TimeSpan idleThreshold)
    {
        if (lastActivity is { } la && now - la > idleThreshold)
        {
            return new TaskStatus(TaskState.Idle, lastActivity);
        }

        var state = last.Role switch
        {
            EventRole.Assistant when last.ToolUseCount > 0 => MapTool(last.LastToolName),
            EventRole.Assistant when last.HasThinking && !last.HasText => TaskState.Thinking,
            EventRole.Assistant => TaskState.Generating,
            EventRole.User when last.IsToolResultError => TaskState.Error,
            EventRole.User => TaskState.Working,
            _ => TaskState.Working,
        };

        return new TaskStatus(state, lastActivity);
    }

    private static TaskState MapTool(string? tool) => tool switch
    {
        "Read" or "NotebookRead" => TaskState.ReadingFile,
        "Write" or "Edit" or "NotebookEdit" => TaskState.WritingFile,
        "Grep" or "Glob" or "WebSearch" => TaskState.Searching,
        _ => TaskState.CallingTool,
    };
}
