using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AiCodingHub.Core.Messaging;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Options;

namespace AiCodingHub.App.Widgets;

/// <summary>
/// Hosts the pluggable widget collection: it owns which widgets are enabled (from
/// <see cref="WidgetOptions"/>) and visible (compact mode), and fans each incoming
/// <see cref="SnapshotUpdatedMessage"/> out to every enabled widget on the UI thread.
/// <para>
/// Snapshots arrive from several background loops; they are coalesced — only the newest is
/// kept and applied by one non-blocking <c>BeginInvoke</c> — so a busy UI never stalls a
/// collector and stale snapshots are dropped instead of queued.
/// </para>
/// Also projects the one-line footer (today's spend, usage, CPU/RAM).
/// </summary>
public sealed partial class WidgetHostViewModel : ObservableObject
{
    private readonly IReadOnlyList<WidgetViewModel> _registered;

    /// <summary>Enabled widgets in display order (may be hidden in compact mode).</summary>
    private IReadOnlyList<WidgetViewModel> _enabled;
    private bool _compact;

    private HudSnapshot? _pending;
    private int _scheduled;

    /// <summary>The widgets currently shown, in order — rebuilt on option/compact changes.</summary>
    public ObservableCollection<WidgetViewModel> VisibleWidgets { get; } = new();

    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string _statusLine = "waiting for a Claude session…";
    [ObservableProperty] private string _sessionCount = "";
    [ObservableProperty] private string _todayCost = "$0.00";
    [ObservableProperty] private string _weekCost = "";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private bool _hasUsage;
    [ObservableProperty] private string _cpuText = "";
    [ObservableProperty] private string _ramText = "";

    public WidgetHostViewModel(
        IEnumerable<WidgetViewModel> widgets,
        IOptionsMonitor<WidgetOptions> options,
        IMessenger messenger)
    {
        _registered = widgets.OrderBy(w => w.Order).ToArray();
        _enabled = Filter(options.CurrentValue);
        Rebuild();

        options.OnChange(o => Post(() =>
        {
            _enabled = Filter(o);
            Rebuild();
        }));

        messenger.Register<WidgetHostViewModel, SnapshotUpdatedMessage>(
            this, static (recipient, message) => recipient.OnSnapshot(message.Snapshot));
    }

    /// <summary>Toggles compact mode; hides non-essential widgets without losing their state.</summary>
    public void SetCompact(bool compact)
    {
        if (_compact == compact)
        {
            return;
        }

        _compact = compact;
        Rebuild();
    }

    private void OnSnapshot(HudSnapshot snapshot)
    {
        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _scheduled, 1) == 0)
        {
            Post(ApplyPending);
        }
    }

    private void ApplyPending()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        var snapshot = Interlocked.Exchange(ref _pending, null);
        if (snapshot is null)
        {
            return;
        }

        var sessions = snapshot.Groups.Sum(g => g.Sessions.Count);
        var live = snapshot.Groups.Sum(g => g.LiveCount);
        HasSession = sessions > 0 || snapshot.HasActiveSession;
        SessionCount = sessions == 0 ? "" : live == sessions ? $"{live}" : $"{live}/{sessions}";
        StatusLine = HasSession ? "" : "waiting for a Claude session…";

        TodayCost = SessionsWidgetViewModel.Money(snapshot.Cost.TodayUsd);
        WeekCost = SessionsWidgetViewModel.Money(snapshot.Cost.WeekUsd);
        HasUsage = snapshot.Usage.Available;
        UsageText = snapshot.Usage.Available ? $"{snapshot.Usage.FiveHourPercent:0}%" : "";
        CpuText = $"{snapshot.Perf.ClaudeCpuPercent:0}%";
        RamText = snapshot.Perf.ClaudeRamMb >= 1024
            ? $"{snapshot.Perf.ClaudeRamMb / 1024:0.0}G"
            : $"{snapshot.Perf.ClaudeRamMb:0}M";

        // Update all enabled widgets (even those hidden in compact) so expanding is instant.
        foreach (var widget in _enabled)
        {
            widget.Update(snapshot);
        }
    }

    private IReadOnlyList<WidgetViewModel> Filter(WidgetOptions options) =>
        _registered.Where(w => w.IsEnabled(options)).ToArray();

    private void Rebuild()
    {
        VisibleWidgets.Clear();
        foreach (var widget in _enabled)
        {
            if (!_compact || widget.VisibleInCompact)
            {
                VisibleWidgets.Add(widget);
            }
        }
    }

    private static void Post(System.Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
}
