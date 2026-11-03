using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodingHub.App.Widgets;

/// <summary>Session + today spend in USD (FR-3). Essential — shown in compact.</summary>
public sealed partial class CostWidgetViewModel : WidgetViewModel
{
    public override string Key => "Cost";
    public override int Order => 40;
    public override bool VisibleInCompact => true;
    public override bool IsEnabled(WidgetOptions options) => options.Cost;

    [ObservableProperty] private string _session = "$0.00";
    [ObservableProperty] private string _today = "$0.00";

    public override void Update(HudSnapshot snapshot)
    {
        Session = $"${snapshot.Cost.SessionUsd:0.00}";
        Today = $"${snapshot.Cost.TodayUsd:0.00} today";
    }
}
