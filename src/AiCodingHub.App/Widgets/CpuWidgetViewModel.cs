using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodingHub.App.Widgets;

/// <summary>System + Claude-process CPU load (FR-5).</summary>
public sealed partial class CpuWidgetViewModel : WidgetViewModel
{
    public override string Key => "Cpu";
    public override int Order => 70;
    public override bool IsEnabled(WidgetOptions options) => options.Cpu;

    [ObservableProperty] private string _total = "0%";
    [ObservableProperty] private string _claude = "";

    public override void Update(HudSnapshot snapshot)
    {
        Total = $"{snapshot.Perf.TotalCpuPercent:0}%";
        Claude = $"claude {snapshot.Perf.ClaudeCpuPercent:0}%";
    }
}
