using System;
using System.Collections.Generic;

namespace AiCodingHub.Core.Models;

/// <summary>
/// Immutable, point-in-time composite of every metric group shown on the HUD. Produced by
/// the aggregator and published on the messenger for ViewModels to project.
/// </summary>
public sealed record HudSnapshot(
    DateTimeOffset Timestamp,
    bool HasActiveSession,
    SessionInfo Session,
    ContextInfo Context,
    CostInfo Cost,
    RequestStats Requests,
    TaskStatus Task,
    PerfInfo Perf,
    UsageInfo Usage)
{
    /// <summary>Every monitored session, grouped by repo (leader first).</summary>
    public IReadOnlyList<SessionGroup> Groups { get; init; } = Array.Empty<SessionGroup>();

    public static HudSnapshot Empty { get; } = new(
        default,
        false,
        SessionInfo.Empty,
        ContextInfo.Empty,
        CostInfo.Empty,
        RequestStats.Empty,
        TaskStatus.Empty,
        PerfInfo.Empty,
        UsageInfo.Empty);
}
