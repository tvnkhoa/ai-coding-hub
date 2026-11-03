using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Models;

namespace AiCodingHub.Infrastructure.Hosting;

/// <summary>
/// Keeps the last-known slice from each provider and composes them into a full
/// <see cref="HudSnapshot"/> with a fresh timestamp. Thread-safe so the polling, cost and
/// metrics loops can update concurrently.
/// </summary>
public sealed class SnapshotAggregator : ISnapshotAggregator
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private ClaudeSnapshot _claude = ClaudeSnapshot.Empty;
    private CostTotals _costTotals = CostTotals.Empty;
    private PerfInfo _perf = PerfInfo.Empty;
    private UsageInfo _usage = UsageInfo.Empty;

    public SnapshotAggregator(TimeProvider time) => _time = time;

    public HudSnapshot Current
    {
        get { lock (_gate) { return Build(); } }
    }

    public HudSnapshot ApplyClaude(ClaudeSnapshot claude)
    {
        lock (_gate)
        {
            _claude = claude;
            return Build();
        }
    }

    public HudSnapshot ApplyCostTotals(CostTotals totals)
    {
        lock (_gate)
        {
            _costTotals = totals;
            return Build();
        }
    }

    public HudSnapshot ApplyPerf(PerfInfo perf)
    {
        lock (_gate)
        {
            _perf = perf;
            return Build();
        }
    }

    public HudSnapshot ApplyUsage(UsageInfo usage)
    {
        lock (_gate)
        {
            _usage = usage;
            return Build();
        }
    }

    private HudSnapshot Build() => new(
        Timestamp: _time.GetUtcNow(),
        HasActiveSession: _claude.HasActiveSession,
        Session: _claude.Session,
        Context: _claude.Context,
        Cost: _claude.Cost with
        {
            TodayUsd = _costTotals.TodayUsd,
            WeekUsd = _costTotals.WeekUsd,
            MonthUsd = _costTotals.MonthUsd,
        },
        Requests: _claude.Requests,
        Task: _claude.Task,
        Perf: _perf,
        Usage: _usage)
    {
        Groups = _claude.Groups,
    };
}
