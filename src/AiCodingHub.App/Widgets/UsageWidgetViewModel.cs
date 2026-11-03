using System;
using System.Windows.Threading;
using AiCodingHub.Core.Messaging;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AiCodingHub.App.Widgets;

/// <summary>
/// Subscription usage limits — the 5-hour and weekly windows shown by Claude Code's
/// <c>/usage</c>. Populated only when a probe token is configured; otherwise shows a hint.
/// A manual "refresh now" button triggers an out-of-cycle probe.
/// </summary>
public sealed partial class UsageWidgetViewModel : WidgetViewModel
{
    private readonly IMessenger _messenger;
    private readonly DispatcherTimer _spinnerTimeout;

    public UsageWidgetViewModel(IMessenger messenger)
    {
        _messenger = messenger;
        // Auto-clears the "refreshing" indicator if a probe is slow or fails, since Update() runs
        // on every snapshot and cannot tell which one carries the fresh usage slice.
        _spinnerTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _spinnerTimeout.Tick += (_, _) => { _spinnerTimeout.Stop(); IsRefreshing = false; };
    }

    public override string Key => "Usage";
    public override int Order => 45;
    public override bool VisibleInCompact => true;
    public override bool IsEnabled(WidgetOptions options) => options.Usage;

    private UsageInfo? _lastSeen;

    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private double _fiveHourPercent;
    [ObservableProperty] private string _fiveHourText = "—";
    [ObservableProperty] private string _fiveHourReset = "";
    [ObservableProperty] private double _weeklyPercent;
    [ObservableProperty] private string _weeklyText = "—";
    [ObservableProperty] private string _weeklyReset = "";
    [ObservableProperty] private string _hint = "enable Usage token in settings";

    /// <summary>Requests an immediate probe (button); the collector wakes and re-probes now.</summary>
    [RelayCommand]
    private void Refresh()
    {
        IsRefreshing = true;
        _spinnerTimeout.Stop();
        _spinnerTimeout.Start();
        _messenger.Send(new RefreshUsageRequestedMessage());
    }

    public override void Update(HudSnapshot snapshot)
    {
        var u = snapshot.Usage;

        // The provider hands out a new UsageInfo instance only after a probe; a changed reference
        // means the refresh actually completed, so stop the spinner (Update runs on every tick).
        if (!ReferenceEquals(u, _lastSeen))
        {
            _lastSeen = u;
            if (IsRefreshing)
            {
                _spinnerTimeout.Stop();
                IsRefreshing = false;
            }
        }

        HasData = u.Available;
        if (!u.Available)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        FiveHourPercent = u.FiveHourPercent;
        FiveHourText = $"{u.FiveHourPercent:0}%";
        FiveHourReset = Countdown(u.FiveHourResetAt, now);

        WeeklyPercent = u.WeeklyPercent;
        WeeklyText = $"{u.WeeklyPercent:0}%";
        WeeklyReset = Countdown(u.WeeklyResetAt, now);
    }

    private static string Countdown(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not { } at)
        {
            return "";
        }

        var remaining = at - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "resets now";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"resets {(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        return remaining.TotalHours >= 1
            ? $"resets {(int)remaining.TotalHours}h {remaining.Minutes}m"
            : $"resets {remaining.Minutes}m";
    }
}
