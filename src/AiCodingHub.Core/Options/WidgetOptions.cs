namespace AiCodingHub.Core.Options;

/// <summary>
/// Per-widget visibility toggles. Bound from the "Widgets" configuration section. The
/// multi-session dashboard (<see cref="Sessions"/>) is the main view; the single-session
/// detail widgets are off by default and can be turned back on.
/// </summary>
public sealed class WidgetOptions
{
    public const string SectionName = "Widgets";

    /// <summary>Every monitored session grouped by repo (leader / members).</summary>
    public bool Sessions { get; set; } = true;

    public bool Model { get; set; }
    public bool Context { get; set; }
    public bool Cost { get; set; }
    public bool Cpu { get; set; }
    public bool Ram { get; set; }
    public bool Session { get; set; }
    public bool History { get; set; }
    public bool Notification { get; set; } = true;

    /// <summary>Subscription 5h/weekly usage bars. Requires a configured OAuth token to show data.</summary>
    public bool Usage { get; set; }
}
