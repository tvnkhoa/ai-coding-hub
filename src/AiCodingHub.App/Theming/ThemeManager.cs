using System;
using System.Windows;
using System.Windows.Threading;
using AiCodingHub.Core.Options;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace AiCodingHub.App.Theming;

/// <summary>
/// Applies the Dark/Light/Auto theme by swapping the theme <see cref="ResourceDictionary"/> in
/// <see cref="Application.Resources"/>. Reacts to <c>HudOptions.Theme</c> changes; "Auto"
/// follows the Windows apps-theme setting, including changes made while the HUD runs.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private readonly IOptionsMonitor<HudOptions> _options;

    public ThemeManager(IOptionsMonitor<HudOptions> options)
    {
        _options = options;
        _options.OnChange(_ => Post(Apply));
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>True when the light palette is active.</summary>
    public bool IsLight { get; private set; }

    /// <summary>Raised on the UI thread after the palette changes (argument: is light).</summary>
    public event Action<bool>? ThemeChanged;

    public void Initialize() => Apply();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General
            && string.Equals(_options.CurrentValue.Theme, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            Post(Apply);
        }
    }

    private void Apply()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var isLight = Resolve(_options.CurrentValue.Theme);
        var source = new Uri(isLight ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        var next = new ResourceDictionary { Source = source };

        var dictionaries = app.Resources.MergedDictionaries;
        for (var i = dictionaries.Count - 1; i >= 0; i--)
        {
            var src = dictionaries[i].Source?.OriginalString ?? string.Empty;
            if (src.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(i);
            }
        }

        // Insert before Controls.xaml so brush lookups resolve (DynamicResource is order-agnostic anyway).
        dictionaries.Insert(0, next);

        IsLight = isLight;
        ThemeChanged?.Invoke(isLight);
    }

    private static bool Resolve(string theme) => theme?.ToLowerInvariant() switch
    {
        "light" => true,
        "dark" => false,
        _ => IsSystemLight(),
    };

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false; // default dark
        }
    }

    private static void Post(Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
