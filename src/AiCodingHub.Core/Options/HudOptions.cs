namespace AiCodingHub.Core.Options;

/// <summary>
/// Global HUD behaviour. Bound from the "Hud" configuration section via
/// <c>IOptionsMonitor&lt;HudOptions&gt;</c> so changes hot-reload at runtime.
/// </summary>
public sealed class HudOptions
{
    public const string SectionName = "Hud";

    /// <summary>Primary refresh cadence (250 / 500 / 1000 / 2000 ms).</summary>
    public int RefreshIntervalMs { get; set; } = 500;

    /// <summary>"Dark" | "Light" | "Auto".</summary>
    public string Theme { get; set; } = "Auto";

    /// <summary>Background opacity percentage (60–100). Text always stays fully opaque.</summary>
    public int Opacity { get; set; } = 100;

    /// <summary>UI font family (e.g. "Segoe UI Variable Text", "JetBrains Mono").</summary>
    public string Font { get; set; } = "Segoe UI";

    /// <summary>Minutes of no transcript activity before a session is reported Idle.</summary>
    public int IdleMinutes { get; set; } = 5;

    /// <summary>
    /// Override for the Claude projects root. When null, defaults to
    /// <c>%USERPROFILE%\.claude\projects</c>.
    /// </summary>
    public string? ClaudeProjectsRoot { get; set; }

    /// <summary>
    /// Override for the working-day handover directory (session registry + leader locks).
    /// When null, defaults to <c>%USERPROFILE%\.claude\handover</c>.
    /// </summary>
    public string? HandoverDir { get; set; }

    /// <summary>
    /// Folder of live-prompt markers written by the optional <c>tools/hooks/hud-decision.mjs</c>
    /// hook; empty = <c>%USERPROFILE%\.claude\hud-pending</c>. Without the hook, a question is
    /// only seen once Claude Code writes it to the transcript.
    /// </summary>
    public string? DecisionMarkerDir { get; set; }

    /// <summary>Most sessions shown at once (most recently active first).</summary>
    public int MaxSessions { get; set; } = 12;

    /// <summary>
    /// Sessions not in today's registry are shown while their transcript changed within this
    /// many minutes.
    /// </summary>
    public int ActiveWindowMinutes { get; set; } = 30;

    /// <summary>Minutes an ended session (its process exited) stays visible; 0 = removed at once.</summary>
    public int EndedHideMinutes { get; set; }

    /// <summary>
    /// Local hour the cost "today" bucket starts (0 = midnight; 4 matches a 04:00→04:00
    /// working day).
    /// </summary>
    public int DayStartHour { get; set; }
}
