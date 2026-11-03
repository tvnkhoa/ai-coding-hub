using System;
using System.Collections.Generic;

namespace AiCodingHub.Core.Models;

/// <summary>
/// The slice produced by <c>IClaudeProvider</c>. The positional fields describe the
/// <b>primary</b> session (the focused one, else the most recently active) so single-session
/// consumers (alerts, history) keep working; <see cref="Groups"/> carries every monitored
/// session grouped by repo for the dashboard.
/// </summary>
public sealed record ClaudeSnapshot(
    bool HasActiveSession,
    SessionInfo Session,
    ContextInfo Context,
    CostInfo Cost,
    RequestStats Requests,
    TaskStatus Task)
{
    /// <summary>All monitored sessions, grouped by repo (empty when none).</summary>
    public IReadOnlyList<SessionGroup> Groups { get; init; } = Array.Empty<SessionGroup>();

    public static ClaudeSnapshot Empty { get; } = new(
        false,
        SessionInfo.Empty,
        ContextInfo.Empty,
        CostInfo.Empty,
        RequestStats.Empty,
        TaskStatus.Empty);
}
