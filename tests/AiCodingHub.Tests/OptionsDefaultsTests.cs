using AiCodingHub.Core.Options;

namespace AiCodingHub.Tests;

/// <summary>
/// Phase 2 sanity: confirms Core options POCOs carry the documented defaults and the
/// test project builds/runs against Core + Infrastructure.
/// </summary>
public class OptionsDefaultsTests
{
    [Fact]
    public void HudOptions_HaveExpectedDefaults()
    {
        var hud = new HudOptions();

        Assert.Equal(500, hud.RefreshIntervalMs);
        Assert.Equal("Auto", hud.Theme);
        Assert.Equal(90, hud.Opacity);
        Assert.Equal(5, hud.IdleMinutes);
        Assert.Null(hud.ClaudeProjectsRoot);
    }

    [Fact]
    public void AlertOptions_ContextThresholds_AreAscending()
    {
        var alerts = new AlertOptions();

        Assert.True(alerts.ContextWarnPercent < alerts.ContextHighPercent);
        Assert.True(alerts.ContextHighPercent < alerts.ContextCriticalPercent);
        Assert.Equal(5.0, alerts.CostThresholdUsd);
    }

    [Fact]
    public void UsageOptions_DefaultOff_WithSafeDefaults()
    {
        var usage = new UsageOptions();

        Assert.False(usage.Enabled);
        Assert.Null(usage.OAuthToken);
        Assert.Equal(5, usage.RefreshMinutes);
        Assert.Equal("claude-haiku-4-5-20251001", usage.PollModel);
    }

    [Fact]
    public void WindowOptions_DefaultToFullSize()
    {
        var window = new WindowOptions();

        Assert.Equal(300, window.Width);
        Assert.Equal(420, window.Height);
        Assert.Equal("Full", window.Mode);
        Assert.True(window.Topmost);
    }
}
