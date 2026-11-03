using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodingHub.App.Widgets;

/// <summary>Active model + Claude CLI version (FR-1).</summary>
public sealed partial class ModelWidgetViewModel : WidgetViewModel
{
    public override string Key => "Model";
    public override int Order => 10;
    public override bool IsEnabled(WidgetOptions options) => options.Model;

    [ObservableProperty] private string _model = "—";
    [ObservableProperty] private string _version = "";

    public override void Update(HudSnapshot snapshot)
    {
        Model = snapshot.Session.Model;
        Version = snapshot.Session.ClaudeVersion is "—" or ""
            ? ""
            : $"v{snapshot.Session.ClaudeVersion}";
    }
}
