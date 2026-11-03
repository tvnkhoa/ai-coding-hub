namespace AiCodingHub.Core.Options;

/// <summary>
/// Overlay window placement and behaviour. Bound from the "Window" section and
/// persisted back to the user settings layer when the user moves/resizes the HUD.
/// </summary>
public sealed class WindowOptions
{
    public const string SectionName = "Window";

    /// <summary>Last screen position; null means "let the OS/default place it".</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }

    public double Width { get; set; } = 300;
    public double Height { get; set; } = 420;

    /// <summary>"Full" (300×420) | "Compact" (300×150).</summary>
    public string Mode { get; set; } = "Full";

    public bool Topmost { get; set; } = true;
}
