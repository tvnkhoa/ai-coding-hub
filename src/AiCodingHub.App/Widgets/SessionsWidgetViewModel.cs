using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using AiCodingHub.App.Interop;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;

namespace AiCodingHub.App.Widgets;

/// <summary>
/// The main dashboard: every monitored session as one compact row (name, model, context), grouped
/// by repo with the working-day leader first. Collections are diffed in place (not rebuilt) so
/// expand state and bar animations survive each 500 ms update.
/// </summary>
public sealed partial class SessionsWidgetViewModel : WidgetViewModel
{
    private readonly IOptionsMonitor<AlertOptions> _alerts;

    public SessionsWidgetViewModel(IOptionsMonitor<AlertOptions> alerts) => _alerts = alerts;

    public override string Key => "Sessions";
    public override int Order => 5;
    public override bool VisibleInCompact => true;
    public override bool IsEnabled(WidgetOptions options) => options.Sessions;

    public ObservableCollection<SessionGroupViewModel> Groups { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;

    public override void Update(HudSnapshot snapshot)
    {
        var opts = _alerts.CurrentValue;
        Sync(Groups, snapshot.Groups, g => g.Name, g => new SessionGroupViewModel(g.Name), (vm, g) => vm.Update(g, opts));
        IsEmpty = Groups.Count == 0;
    }

    /// <summary>Severity band of a context percentage (0 ok · 1 warn · 2 high · 3 critical).</summary>
    internal static int Level(double pct, AlertOptions o) =>
        pct >= o.ContextCriticalPercent ? 3 : pct >= o.ContextHighPercent ? 2 : pct >= o.ContextWarnPercent ? 1 : 0;

    internal static string Money(decimal usd) => usd switch
    {
        >= 100m => usd.ToString("$0", CultureInfo.InvariantCulture),
        _ => usd.ToString("$0.00", CultureInfo.InvariantCulture),
    };

    internal static string Tokens(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString(n % 1_000_000 == 0 ? "0" : "0.0#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1_000.0).ToString("0", CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"4m", "1h 05m", "now" — how long something has been waiting.</summary>
    internal static string Waited(DateTimeOffset? since, DateTimeOffset now)
    {
        if (since is not { } s)
        {
            return "";
        }

        var d = now - s;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m";
        return $"{(int)d.TotalHours}h {d.Minutes:00}m";
    }

    /// <summary>Reorders/inserts/removes <paramref name="target"/> in place to match <paramref name="source"/>.</summary>
    internal static void Sync<TVm, TModel>(
        ObservableCollection<TVm> target,
        IReadOnlyList<TModel> source,
        Func<TModel, string> key,
        Func<TModel, TVm> create,
        Action<TVm, TModel> update)
        where TVm : IKeyed
    {
        for (var i = 0; i < source.Count; i++)
        {
            var k = key(source[i]);
            var existing = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (string.Equals(target[j].Key, k, StringComparison.OrdinalIgnoreCase))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                target.Insert(i, create(source[i]));
            }
            else if (existing != i)
            {
                target.Move(existing, i);
            }

            update(target[i], source[i]);
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

/// <summary>Anything diffed by key in <see cref="SessionsWidgetViewModel.Sync"/>.</summary>
public interface IKeyed
{
    string Key { get; }
}

/// <summary>One repo: header (name, live count, worst context, total cost) + its session rows.</summary>
public sealed partial class SessionGroupViewModel : ObservableObject, IKeyed
{
    public SessionGroupViewModel(string name) => Name = name;

    public string Key => Name;
    public string Name { get; }

    public ObservableCollection<SessionRowViewModel> Rows { get; } = new();

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private string _liveText = "";
    [ObservableProperty] private string _costText = "";
    [ObservableProperty] private double _maxFraction;
    [ObservableProperty] private string _maxPercentText = "";
    [ObservableProperty] private int _maxLevel;
    [ObservableProperty] private bool _needsDecision;

    /// <summary>Any session here is active: the card border shows the "working" orbit.</summary>
    [ObservableProperty] private bool _isWorking;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    internal void Update(SessionGroup group, AlertOptions opts)
    {
        var live = group.LiveCount;
        LiveText = live == group.Sessions.Count ? $"{live} live" : $"{live}/{group.Sessions.Count} live";
        CostText = SessionsWidgetViewModel.Money(group.TotalCostUsd);
        MaxFraction = group.MaxContextPercent / 100.0;
        MaxPercentText = $"{group.MaxContextPercent:0}%";
        MaxLevel = SessionsWidgetViewModel.Level(group.MaxContextPercent, opts);
        NeedsDecision = group.NeedsDecision;
        IsWorking = group.Sessions.Any(s => s.Activity == SessionActivity.Active);

        SessionsWidgetViewModel.Sync(Rows, group.Sessions, s => s.SessionId, s => new SessionRowViewModel(s.SessionId),
            (vm, s) => vm.Update(s, opts));
    }
}

/// <summary>One session row: role · name (id below) · model · context bar · % · state dot.</summary>
public sealed partial class SessionRowViewModel : ObservableObject, IKeyed
{
    private int? _pid;

    public SessionRowViewModel(string sessionId)
    {
        SessionId = sessionId;
        ShortId = sessionId.Length > 8 ? sessionId[..8] : sessionId;
    }

    public string Key => SessionId;
    public string SessionId { get; }
    public string ShortId { get; }

    [ObservableProperty] private bool _isLeader;
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _modelLabel = "—";
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string _percentText = "0%";
    [ObservableProperty] private int _level;
    [ObservableProperty] private SessionActivity _activity;
    [ObservableProperty] private bool _isFocused;
    [ObservableProperty] private bool _needsDecision;
    [ObservableProperty] private string _toolTipText = "";

    /// <summary>Double-click a row: bring its terminal window forward.</summary>
    [RelayCommand]
    private void Open()
    {
        if (_pid is { } pid)
        {
            WindowActivator.TryFocusSession(pid);
        }
    }

    internal void Update(SessionSummary s, AlertOptions opts)
    {
        _pid = s.Pid;
        IsLeader = s.Role == SessionRole.Leader;
        DisplayName = s.DisplayName;
        ModelLabel = s.ModelLabel;
        Fraction = s.ContextPercent / 100.0;
        PercentText = $"{s.ContextPercent:0}%";
        Level = SessionsWidgetViewModel.Level(s.ContextPercent, opts);
        Activity = s.Activity;
        IsFocused = s.IsFocused;
        NeedsDecision = s.Decision is not null;

        var role = s.Role switch { SessionRole.Leader => "Leader", SessionRole.Member => "Member", _ => "Session" };
        var lines = new List<string>
        {
            $"{s.DisplayName} · {role}",
            $"{s.SessionId}",
            $"{s.Model}",
            $"Context {SessionsWidgetViewModel.Tokens(s.ContextTokens)} / {SessionsWidgetViewModel.Tokens(s.MaxTokens)} ({s.ContextPercent:0.0}%)",
            $"Cost {SessionsWidgetViewModel.Money(s.SessionCostUsd)} · {s.Requests} requests",
        };
        if (s.Decision is { } d)
        {
            lines.Add(d.Kind == "plan" ? "Waiting for you: plan approval" : $"Waiting for you: {d.Header}");
        }
        if (!string.IsNullOrEmpty(s.Workspace)) lines.Add(s.Workspace);
        var state = s.Activity switch
        {
            SessionActivity.Ended => "ended",
            SessionActivity.Idle => "idle",
            _ => "active",
        };
        var last = s.LastActivityAt is { } at ? $" · last activity {at.ToLocalTime():HH:mm:ss}" : "";
        lines.Add(s.Pid is { } pid ? $"PID {pid} · {state}{last}" : $"{state}{last}");
        if (s.Pid is not null) lines.Add("Double-click to open its terminal");
        ToolTipText = string.Join(Environment.NewLine, lines);
    }
}

/// <summary>One "needs you" strip above the dashboard: a session blocked on the owner's answer.</summary>
public sealed partial class DecisionStripViewModel : ObservableObject, IKeyed
{
    private int? _pid;

    public DecisionStripViewModel(string key) => Key = key;

    public string Key { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _waited = "";
    [ObservableProperty] private string _toolTipText = "";

    [RelayCommand]
    private void Open()
    {
        if (_pid is { } pid)
        {
            WindowActivator.TryFocusSession(pid);
        }
    }

    internal void Update(string group, SessionSummary s, DateTimeOffset now)
    {
        _pid = s.Pid;
        var d = s.Decision!;
        Title = $"{group} · {s.DisplayName} needs you";
        Waited = SessionsWidgetViewModel.Waited(d.Since, now);
        ToolTipText = d.Kind == "plan"
            ? "Plan ready for approval — click to open the terminal"
            : $"{d.Header}: {d.Question}{Environment.NewLine}Click to open the terminal";
    }
}
