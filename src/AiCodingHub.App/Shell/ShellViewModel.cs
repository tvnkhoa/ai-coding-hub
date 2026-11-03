using System;
using System.Windows;
using System.Windows.Threading;
using AiCodingHub.App.Settings;
using AiCodingHub.App.Theming;
using AiCodingHub.App.Widgets;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;

namespace AiCodingHub.App.Shell;

/// <summary>
/// Window-level state (title, background opacity, font, mode, backdrop) and commands. The live
/// metric content is delegated to <see cref="WidgetHostViewModel"/> — the shell owns only the
/// chrome and forwards compact-mode changes to the host.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "Mini Monitor";

    /// <summary>Opacity of the background tint only — text and bars stay fully opaque.</summary>
    [ObservableProperty] private double _backgroundOpacity = 0.9;
    [ObservableProperty] private string _font = "Segoe UI";

    /// <summary>True when the Windows 11 acrylic backdrop is active behind the tint.</summary>
    [ObservableProperty] private bool _useBackdrop;

    // Window mode / behaviour.
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _isPinned = true;   // always-on-top (safe, reversible)

    /// <summary>True while the light palette is active (drives the sun/moon toggle).</summary>
    [ObservableProperty] private bool _isLight;

    private readonly IUserSettingsWriter _writer;

    /// <summary>The pluggable widget collection rendered in the body.</summary>
    public WidgetHostViewModel Host { get; }

    /// <summary>Transient alert banner shown over the body.</summary>
    public AlertBannerViewModel Banner { get; }

    public ShellViewModel(
        IOptionsMonitor<HudOptions> hud,
        IOptionsMonitor<WindowOptions> window,
        WidgetHostViewModel host,
        AlertBannerViewModel banner,
        ThemeManager theme,
        IUserSettingsWriter writer)
    {
        Host = host;
        Banner = banner;
        _writer = writer;
        IsLight = theme.IsLight;
        theme.ThemeChanged += isLight => IsLight = isLight;
        Apply(hud.CurrentValue, window.CurrentValue);
        host.SetCompact(IsCompact);

        hud.OnChange(h => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            BackgroundOpacity = ClampOpacity(h.Opacity);
            Font = h.Font;
        }, DispatcherPriority.Normal));
    }

    private void Apply(HudOptions hud, WindowOptions window)
    {
        BackgroundOpacity = ClampOpacity(hud.Opacity);
        Font = hud.Font;
        IsCompact = IsCompactMode(window.Mode);
        IsPinned = window.Topmost;
    }

    partial void OnIsCompactChanged(bool value) => Host.SetCompact(value);

    private static double ClampOpacity(int percent) => Math.Clamp(percent, 60, 100) / 100.0;

    private static bool IsCompactMode(string mode) =>
        string.Equals(mode, "Compact", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void ToggleCompact() => IsCompact = !IsCompact;

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    /// <summary>Switches Dark ⇄ Light and saves it (replacing "Auto"); the theme hot-reloads.</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        IsLight = !IsLight;
        _writer.SaveTheme(IsLight ? "Light" : "Dark");
    }

    [RelayCommand]
    private static void Exit() => Application.Current.Shutdown();
}
