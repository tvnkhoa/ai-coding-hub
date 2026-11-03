using System;
using System.Collections.Generic;
using System.Linq;

namespace AiCodingHub.Core.Models;

/// <summary>Role of a session in the owner's working-day process (from the handover registry).</summary>
public enum SessionRole
{
    /// <summary>Not in the registry (no git repo, or the hook did not run).</summary>
    None,
    Leader,
    Member,
}

/// <summary>Coarse liveness shown per row — the dashboard favours overview over task detail.</summary>
public enum SessionActivity
{
    /// <summary>The transcript changed within the idle threshold.</summary>
    Active,

    /// <summary>The process is alive (or unknown) but the transcript is quiet.</summary>
    Idle,

    /// <summary>The registry PID no longer runs.</summary>
    Ended,
}

/// <summary>One monitored Claude Code session (one transcript file).</summary>
public sealed record SessionSummary(
    string SessionId,
    string Group,
    SessionRole Role,
    string Model,
    string ModelLabel,
    string ProjectName,
    string Workspace,
    long ContextTokens,
    long MaxTokens,
    decimal SessionCostUsd,
    int Requests,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastActivityAt,
    int? Pid,
    bool IsFocused,
    SessionActivity Activity)
{
    public double ContextPercent => MaxTokens <= 0
        ? 0
        : Math.Clamp(ContextTokens * 100.0 / MaxTokens, 0, 100);

    /// <summary>First 8 characters of the session id (what the owner sees in briefs and reports).</summary>
    public string ShortId => SessionId.Length > 8 ? SessionId[..8] : SessionId;
}

/// <summary>Sessions of one repo, leader first, then members by start time.</summary>
public sealed record SessionGroup(string Name, IReadOnlyList<SessionSummary> Sessions)
{
    public SessionSummary? Leader => Sessions.FirstOrDefault(s => s.Role == SessionRole.Leader);

    public decimal TotalCostUsd => Sessions.Sum(s => s.SessionCostUsd);

    public double MaxContextPercent => Sessions.Count == 0 ? 0 : Sessions.Max(s => s.ContextPercent);

    public int LiveCount => Sessions.Count(s => s.Activity != SessionActivity.Ended);
}

/// <summary>Spend across every transcript, bucketed by (working) day / week / month.</summary>
public sealed record CostTotals(decimal TodayUsd, decimal WeekUsd, decimal MonthUsd)
{
    public static CostTotals Empty { get; } = new(0m, 0m, 0m);
}
