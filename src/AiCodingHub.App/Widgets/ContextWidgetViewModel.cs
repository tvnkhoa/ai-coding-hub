using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodingHub.App.Widgets;

/// <summary>Context-window usage bar + token counts (FR-2). Essential — shown in compact.</summary>
public sealed partial class ContextWidgetViewModel : WidgetViewModel
{
    public override string Key => "Context";
    public override int Order => 30;
    public override bool VisibleInCompact => true;
    public override bool IsEnabled(WidgetOptions options) => options.Context;

    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _tokens = "0 / 0";

    public override void Update(HudSnapshot snapshot)
    {
        Percent = snapshot.Context.Percentage;
        Tokens = $"{snapshot.Context.CurrentTokens:N0} / {snapshot.Context.MaxTokens:N0}";
    }
}
