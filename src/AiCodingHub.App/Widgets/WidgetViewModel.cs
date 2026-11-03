using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodingHub.App.Widgets;

/// <summary>
/// Base for every HUD widget. A widget is a self-contained ViewModel that projects one slice
/// of the <see cref="HudSnapshot"/> and is rendered by an implicit <c>DataTemplate</c> keyed on
/// its concrete type. Adding a new widget is: subclass this, register it in
/// <see cref="WidgetRegistration"/>, and add a <c>DataTemplate</c> in <c>WidgetTemplates.xaml</c>.
/// </summary>
public abstract class WidgetViewModel : ObservableObject
{
    /// <summary>Stable identity (also the config flag name), used for logging/diagnostics.</summary>
    public abstract string Key { get; }

    /// <summary>Ascending sort order within the HUD body.</summary>
    public abstract int Order { get; }

    /// <summary>Whether this widget stays visible in compact mode (essentials only).</summary>
    public virtual bool VisibleInCompact => false;

    /// <summary>Reads this widget's <see cref="WidgetOptions"/> toggle.</summary>
    public abstract bool IsEnabled(WidgetOptions options);

    /// <summary>Projects the latest snapshot into observable properties. Always called on the UI thread.</summary>
    public abstract void Update(HudSnapshot snapshot);
}
