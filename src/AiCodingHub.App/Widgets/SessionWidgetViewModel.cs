using System;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodingHub.App.Widgets;

/// <summary>Project name + elapsed session duration (FR-1).</summary>
public sealed partial class SessionWidgetViewModel : WidgetViewModel
{
    public override string Key => "Session";
    public override int Order => 20;
    public override bool IsEnabled(WidgetOptions options) => options.Session;

    [ObservableProperty] private string _project = "—";
    [ObservableProperty] private string _duration = "00:00:00";

    public override void Update(HudSnapshot snapshot)
    {
        Project = snapshot.Session.ProjectName;
        Duration = Format(snapshot.Session.Duration);
    }

    private static string Format(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}
