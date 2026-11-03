using System;
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
using CommunityToolkit.Mvvm.Input;
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
/// Also projects the chrome around the widgets: the live-session pill, the "needs you" strips
/// and bell count, and the footer (spend, 5-hour limit with a manual refresh, Claude load).
/// </summary>
public sealed partial class WidgetHostViewModel : ObservableObject
{
    private readonly IReadOnlyList<WidgetViewModel> _registered;
    private readonly IMessenger _messenger;
    private readonly DispatcherTimer _refreshTimeout;

    /// <summary>Enabled widgets in display order (may be hidden in compact mode).</summary>
    private IReadOnlyList<WidgetViewModel> _enabled;
    private bool _compact;

    private HudSnapshot? _pending;
    private int _scheduled;
    private UsageInfo? _lastUsage;

    /// <summary>The widgets currently shown, in order — rebuilt on option/compact changes.</summary>
    public ObservableCollection<WidgetViewModel> VisibleWidgets { get; } = new();

    /// <summary>Sessions waiting for the owner's answer, oldest first.</summary>
    public ObservableCollection<DecisionStripViewModel> Decisions { get; } = new();

    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string _liveCount = "0";
    [ObservableProperty] private string _liveToolTip = "";
    [ObservableProperty] private int _decisionCount;
    [ObservableProperty] private bool _hasDecision;
    [ObservableProperty] private string _todayCost = "$0.00";
    [ObservableProperty] private string _weekCost = "$0.00";
    [ObservableProperty] private bool _hasUsage;
    [ObservableProperty] private double _usageFraction;
    [ObservableProperty] private string _usageText = "—";
    [ObservableProperty] private int _usageLevel;
    [ObservableProperty] private double _weeklyFraction;
    [ObservableProperty] private string _weeklyText = "—";
    [ObservableProperty] private int _weeklyLevel;
    [ObservableProperty] private string _usageToolTip = "Subscription limits are off. Turn on the Usage section in settings.";
    [ObservableProperty] private bool _isRefreshingUsage;
    [ObservableProperty] private string _cpuText = "0%";
    [ObservableProperty] private string _ramText = "0M";

    public WidgetHostViewModel(
        IEnumerable<WidgetViewModel> widgets,
        IOptionsMonitor<WidgetOptions> options,
        IMessenger messenger)
    {
        _registered = widgets.OrderBy(w => w.Order).ToArray();
        _messenger = messenger;
        _enabled = Filter(options.CurrentValue);
        Rebuild();

        // Clears the spinner if a probe is slow or fails (Update cannot tell which snapshot has it).
        _refreshTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimeout.Tick += (_, _) => { _refreshTimeout.Stop(); IsRefreshingUsage = false; };

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

    /// <summary>Asks the usage collector for an immediate, out-of-cycle limit probe.</summary>
    [RelayCommand]
    private void RefreshUsage()
    {
        IsRefreshingUsage = true;
        _refreshTimeout.Stop();
        _refreshTimeout.Start();
        _messenger.Send(new RefreshUsageRequestedMessage());
    }

    /// <summary>The bell: open the session that has waited longest for the owner.</summary>
    [RelayCommand]
    private void OpenFirstDecision() => Decisions.FirstOrDefault()?.OpenCommand.Execute(null);

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

        var now = DateTimeOffset.Now;
        var sessions = snapshot.Groups.Sum(g => g.Sessions.Count);
        var live = snapshot.Groups.Sum(g => g.LiveCount);
        HasSession = sessions > 0 || snapshot.HasActiveSession;
        LiveCount = live.ToString();
        LiveToolTip = sessions == live ? $"{live} live sessions" : $"{live} live of {sessions} sessions";

        var waiting = snapshot.Groups
            .SelectMany(g => g.Sessions.Where(s => s.Decision is not null).Select(s => (Group: g.Name, Session: s)))
            .OrderBy(x => x.Session.Decision!.Since ?? DateTimeOffset.MaxValue)
            .ToList();
        SessionsWidgetViewModel.Sync(Decisions, waiting, x => x.Session.SessionId,
            x => new DecisionStripViewModel(x.Session.SessionId), (vm, x) => vm.Update(x.Group, x.Session, now));
        DecisionCount = waiting.Count;
        HasDecision = waiting.Count > 0;

        TodayCost = SessionsWidgetViewModel.Money(snapshot.Cost.TodayUsd);
        WeekCost = SessionsWidgetViewModel.Money(snapshot.Cost.WeekUsd);
        ApplyUsage(snapshot.Usage, now);
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

    private void ApplyUsage(UsageInfo u, DateTimeOffset now)
    {
        // A new UsageInfo instance only comes from a completed probe: stop the spinner.
        if (!ReferenceEquals(u, _lastUsage))
        {
            _lastUsage = u;
            if (IsRefreshingUsage)
            {
                _refreshTimeout.Stop();
                IsRefreshingUsage = false;
            }
        }

        HasUsage = u.Available;
        if (!u.Available)
        {
            UsageFraction = 0;
            UsageText = "—";
            WeeklyFraction = 0;
            WeeklyText = "—";
            return;
        }

        UsageFraction = u.FiveHourPercent / 100.0;
        UsageText = $"{u.FiveHourPercent:0}%";
        UsageLevel = LimitLevel(u.FiveHourPercent);
        WeeklyFraction = u.WeeklyPercent / 100.0;
        WeeklyText = $"{u.WeeklyPercent:0}%";
        WeeklyLevel = LimitLevel(u.WeeklyPercent);
        UsageToolTip = $"5-hour limit {u.FiveHourPercent:0}%{Reset(u.FiveHourResetAt, now)}"
                       + $"{Environment.NewLine}Weekly limit {u.WeeklyPercent:0}%{Reset(u.WeeklyResetAt, now)}";
    }

    /// <summary>Colour band of a usage limit (0 ok · 1 ≥80% · 2 ≥90% · 3 ≥95%).</summary>
    private static int LimitLevel(double pct) => pct >= 95 ? 3 : pct >= 90 ? 2 : pct >= 80 ? 1 : 0;

    private static string Reset(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } a) return "";
        var left = a - now;
        if (left <= TimeSpan.Zero) return " · resets now";
        if (left.TotalDays >= 1) return $" · resets in {(int)left.TotalDays}d {left.Hours}h";
        return left.TotalHours >= 1 ? $" · resets in {(int)left.TotalHours}h {left.Minutes}m" : $" · resets in {left.Minutes}m";
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

    private static void Post(Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(action, DispatcherPriority.DataBind);
}
