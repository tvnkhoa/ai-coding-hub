using System;
using System.Collections.Generic;
using AiCodingHub.Core.Models;

namespace AiCodingHub.Core.Alerts;

public enum AlertKind
{
    ContextWarn,
    ContextHigh,
    ContextCritical,
    Idle,
    TaskFinished,
    ToolFailed,
    NetworkLost,
    DecisionNeeded
}

public enum AlertSeverity
{
    Info,
    Warning,
    High,
    Critical
}

/// <summary>A raised alert instance.</summary>
public sealed record Alert(
    AlertKind Kind,
    AlertSeverity Severity,
    string Title,
    string Message,
    DateTimeOffset At);

/// <summary>
/// Evaluates successive snapshots and emits edge-triggered, de-duplicated alerts (no repeat
/// while a condition persists; re-arms on recovery). Stateful across calls.
/// </summary>
public interface IAlertEvaluator
{
    IReadOnlyList<Alert> Evaluate(HudSnapshot current, DateTimeOffset now);
}
