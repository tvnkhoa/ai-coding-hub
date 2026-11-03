namespace AiCodingHub.Core.Options;

/// <summary>
/// Alert thresholds and per-alert enable flags. Bound from the "Alerts" section.
/// Consumed by the alert evaluator (Phase 3).
/// </summary>
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Context-usage percentage thresholds (green→yellow→orange→red).</summary>
    public int ContextWarnPercent { get; set; } = 80;
    public int ContextHighPercent { get; set; } = 90;
    public int ContextCriticalPercent { get; set; } = 95;


    public bool EnableContext { get; set; } = true;
    public bool EnableIdle { get; set; } = true;
    public bool EnableTaskFinished { get; set; } = true;
    public bool EnableToolFailed { get; set; } = true;
    public bool EnableNetwork { get; set; } = true;

    /// <summary>Notify when a session waits for the owner's answer (AskUserQuestion / plan approval).</summary>
    public bool EnableDecision { get; set; } = true;
}
