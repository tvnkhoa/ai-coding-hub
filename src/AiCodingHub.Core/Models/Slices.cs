using System;

namespace AiCodingHub.Core.Models;

/// <summary>Session identity + timing (FR-1).</summary>
public sealed record SessionInfo(
    string Model,
    string Workspace,
    string ProjectName,
    string ClaudeVersion,
    DateTimeOffset? StartedAt,
    TimeSpan Duration,
    string Branch)
{
    public static SessionInfo Empty { get; } =
        new("—", "", "—", "—", null, TimeSpan.Zero, "—");
}

/// <summary>Context-window usage (FR-2).</summary>
public sealed record ContextInfo(long CurrentTokens, long MaxTokens)
{
    public long Remaining => Math.Max(0, MaxTokens - CurrentTokens);

    public double Percentage => MaxTokens <= 0
        ? 0
        : Math.Clamp(CurrentTokens * 100.0 / MaxTokens, 0, 100);

    public static ContextInfo Empty { get; } = new(0, 0);
}

/// <summary>Cost across time buckets in USD (FR-3).</summary>
public sealed record CostInfo(
    decimal SessionUsd,
    decimal TodayUsd,
    decimal WeekUsd,
    decimal MonthUsd)
{
    public static CostInfo Empty { get; } = new(0m, 0m, 0m, 0m);
}

/// <summary>Request/tool statistics for the current session (FR-4).</summary>
public sealed record RequestStats(
    int Requests,
    int Prompts,
    int ToolCalls,
    double AvgLatencySeconds,
    double LongestLatencySeconds,
    double AvgTokens)
{
    public static RequestStats Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>Derived task state + when the session last produced an event (FR-8).</summary>
public sealed record TaskStatus(TaskState State, DateTimeOffset? LastActivityAt)
{
    public static TaskStatus Empty { get; } = new(TaskState.Idle, null);
}

/// <summary>System + Claude-process performance (FR-5).</summary>
public sealed record PerfInfo(
    double TotalCpuPercent,
    double TotalRamMb,
    double ClaudeCpuPercent,
    double ClaudeRamMb)
{
    public static PerfInfo Empty { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Subscription usage limits (the 5-hour and weekly windows shown by Claude Code's
/// <c>/usage</c>), read from <c>anthropic-ratelimit-unified-*</c> response headers.
/// <see cref="Available"/> is false until a successful probe has returned data.
/// </summary>
public sealed record UsageInfo(
    bool Available,
    double FiveHourPercent,
    DateTimeOffset? FiveHourResetAt,
    double WeeklyPercent,
    DateTimeOffset? WeeklyResetAt,
    string? Status)
{
    public static UsageInfo Empty { get; } = new(false, 0, null, 0, null, null);
}

/// <summary>One time-series data point for the history charts (FR-10).</summary>
public sealed record HistorySample(
    DateTimeOffset At,
    double ContextPercent,
    decimal SessionCostUsd,
    double TotalCpuPercent,
    double TotalRamMb,
    int Requests);
