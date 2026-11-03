using System;
using System.Collections.Generic;
using System.Linq;
using AiCodingHub.Core.Alerts;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using Microsoft.Extensions.Options;

namespace AiCodingHub.Infrastructure.Notifications;

/// <summary>
/// Edge-triggered, de-duplicated alert evaluation. Holds internal armed-state so an alert
/// fires once when a condition is entered and re-arms only after recovery.
/// </summary>
public sealed class AlertEvaluator : IAlertEvaluator
{
    private readonly IOptionsMonitor<AlertOptions> _options;

    // Highest context level currently alerted (0 none, 1 warn, 2 high, 3 critical) — for the
    // single primary session, and per session id when the snapshot carries session groups.
    private int _contextLevel;
    private readonly Dictionary<string, int> _sessionLevels = new(StringComparer.OrdinalIgnoreCase);
    private bool _costArmed;
    private readonly HashSet<string> _costAlerted = new(StringComparer.OrdinalIgnoreCase);
    private bool _idleArmed;

    public AlertEvaluator(IOptionsMonitor<AlertOptions> options) => _options = options;

    public IReadOnlyList<Alert> Evaluate(HudSnapshot current, DateTimeOffset now)
    {
        var opts = _options.CurrentValue;
        var alerts = new List<Alert>();

        EvaluateContext(current, opts, now, alerts);
        EvaluateCost(current, opts, now, alerts);
        EvaluateIdle(current, opts, now, alerts);

        return alerts;
    }

    private void EvaluateContext(HudSnapshot current, AlertOptions opts, DateTimeOffset now, List<Alert> alerts)
    {
        if (!opts.EnableContext || !current.HasActiveSession)
        {
            _contextLevel = 0;
            _sessionLevels.Clear();
            return;
        }

        if (current.Groups.Count == 0)
        {
            _contextLevel = Raise(current.Context.Percentage, _contextLevel, "Context", opts, now, alerts);
            return;
        }

        // Every monitored session gets its own edge-triggered level, so a member nearing its
        // window is flagged even when another session is the primary.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in current.Groups)
        {
            foreach (var s in group.Sessions)
            {
                seen.Add(s.SessionId);
                if (s.Activity == SessionActivity.Ended)
                {
                    _sessionLevels.Remove(s.SessionId);
                    continue;
                }

                var role = s.Role switch { SessionRole.Leader => "leader ", SessionRole.Member => "member ", _ => "" };
                var label = $"{group.Name} · {role}{s.ShortId}";
                _sessionLevels[s.SessionId] = Raise(
                    s.ContextPercent, _sessionLevels.GetValueOrDefault(s.SessionId), label, opts, now, alerts);
            }
        }

        foreach (var gone in new List<string>(_sessionLevels.Keys))
        {
            if (!seen.Contains(gone))
            {
                _sessionLevels.Remove(gone);
            }
        }
    }

    /// <summary>Raises an alert only when crossing UP to a higher level; returns the new level.</summary>
    private static int Raise(double pct, int previous, string subject, AlertOptions opts, DateTimeOffset now, List<Alert> alerts)
    {
        var level =
            pct >= opts.ContextCriticalPercent ? 3 :
            pct >= opts.ContextHighPercent ? 2 :
            pct >= opts.ContextWarnPercent ? 1 : 0;

        if (level > previous)
        {
            var (kind, severity, label) = level switch
            {
                3 => (AlertKind.ContextCritical, AlertSeverity.Critical, "critical"),
                2 => (AlertKind.ContextHigh, AlertSeverity.High, "high"),
                _ => (AlertKind.ContextWarn, AlertSeverity.Warning, "elevated"),
            };
            var title = subject == "Context" ? $"Context {label}" : $"Context {label} · {subject}";
            alerts.Add(new Alert(kind, severity, title, $"Context usage at {pct:0}%.", now));
        }

        return level;
    }

    private void EvaluateCost(HudSnapshot current, AlertOptions opts, DateTimeOffset now, List<Alert> alerts)
    {
        if (!opts.EnableCost || !current.HasActiveSession)
        {
            _costArmed = false;
            _costAlerted.Clear();
            return;
        }

        if (current.Groups.Count == 0)
        {
            var over = (double)current.Cost.SessionUsd >= opts.CostThresholdUsd;
            if (over && !_costArmed)
            {
                alerts.Add(new Alert(AlertKind.CostExceeded, AlertSeverity.Warning, "Cost threshold",
                    $"Session cost ${current.Cost.SessionUsd:0.00} exceeded ${opts.CostThresholdUsd:0.00}.", now));
                _costArmed = true;
            }
            else if (!over)
            {
                _costArmed = false;
            }
            return;
        }

        // Once per session: the primary session changes as sessions take turns writing, so a
        // single armed flag would re-fire every time it moved between cheap and expensive ones.
        foreach (var group in current.Groups)
        {
            foreach (var s in group.Sessions)
            {
                var over = (double)s.SessionCostUsd >= opts.CostThresholdUsd;
                if (over && _costAlerted.Add(s.SessionId))
                {
                    alerts.Add(new Alert(AlertKind.CostExceeded, AlertSeverity.Warning,
                        $"Cost threshold · {group.Name} {s.ShortId}",
                        $"Session cost ${s.SessionCostUsd:0.00} exceeded ${opts.CostThresholdUsd:0.00}.", now));
                }
                else if (!over)
                {
                    _costAlerted.Remove(s.SessionId);
                }
            }
        }
    }

    private void EvaluateIdle(HudSnapshot current, AlertOptions opts, DateTimeOffset now, List<Alert> alerts)
    {
        if (!opts.EnableIdle || !current.HasActiveSession)
        {
            _idleArmed = false;
            return;
        }

        // With several sessions, "idle" means nothing is running anywhere.
        var idle = current.Groups.Count == 0
            ? current.Task.State == TaskState.Idle
            : current.Groups.All(g => g.Sessions.All(s => s.Activity != SessionActivity.Active));
        if (idle && !_idleArmed)
        {
            alerts.Add(new Alert(AlertKind.Idle, AlertSeverity.Info,
                current.Groups.Count == 0 ? "Claude idle" : "All sessions idle",
                "No session activity for a while.", now));
            _idleArmed = true;
        }
        else if (!idle)
        {
            _idleArmed = false;
        }
    }
}
