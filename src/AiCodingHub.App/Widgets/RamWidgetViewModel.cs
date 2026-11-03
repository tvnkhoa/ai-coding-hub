using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodingHub.App.Widgets;

/// <summary>System + Claude-process memory use (FR-5).</summary>
public sealed partial class RamWidgetViewModel : WidgetViewModel
{
    public override string Key => "Ram";
    public override int Order => 80;
    public override bool IsEnabled(WidgetOptions options) => options.Ram;

    [ObservableProperty] private string _total = "0 MB";
    [ObservableProperty] private string _claude = "";

    public override void Update(HudSnapshot snapshot)
    {
        Total = $"{snapshot.Perf.TotalRamMb:0} MB";
        Claude = $"claude {snapshot.Perf.ClaudeRamMb:0} MB";
    }
}
