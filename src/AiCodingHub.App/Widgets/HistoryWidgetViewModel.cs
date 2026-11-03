using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiCodingHub.App.Widgets;

/// <summary>
/// Context-usage trend sparkline (FR-10). Reads the bounded <see cref="IHistoryStore"/> that
/// <c>HistoryRecorder</c> fills, and projects it to a <see cref="PointCollection"/> a WPF
/// Polyline renders with <c>Stretch=Fill</c> — no charting library or native deps.
/// </summary>
public sealed partial class HistoryWidgetViewModel : WidgetViewModel
{
    private const int MaxPoints = 120;

    private readonly IHistoryStore _store;

    public HistoryWidgetViewModel(IHistoryStore store) => _store = store;

    public override string Key => "History";
    public override int Order => 90;
    public override bool IsEnabled(WidgetOptions options) => options.History;

    [ObservableProperty] private PointCollection _points = new();
    [ObservableProperty] private string _current = "0%";
    [ObservableProperty] private bool _hasData;

    public override void Update(HudSnapshot snapshot)
    {
        Current = $"{snapshot.Context.Percentage:0}%";

        IReadOnlyList<HistorySample> samples = _store.Snapshot();
        if (samples.Count > MaxPoints)
        {
            samples = samples.Skip(samples.Count - MaxPoints).ToArray();
        }

        if (samples.Count < 2)
        {
            HasData = false;
            Points = new PointCollection();
            return;
        }

        // X = sample index, Y = inverted context % (so higher usage plots higher).
        var points = new PointCollection(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            points.Add(new Point(i, 100 - samples[i].ContextPercent));
        }
        points.Freeze();

        Points = points;
        HasData = true;
    }
}
