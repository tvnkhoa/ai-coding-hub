using System.Threading;
using System.Threading.Tasks;
using AiCodingHub.Core.Models;

namespace AiCodingHub.Core.Abstractions;

/// <summary>Reads every monitored Claude Code session transcript and derives the Claude slice.</summary>
public interface IClaudeProvider
{
    Task<ClaudeSnapshot> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Probes the subscription 5-hour / weekly usage limits via a minimal authenticated call.
/// Returns <see cref="UsageInfo.Empty"/> when disabled, unconfigured, or unreachable.
/// </summary>
public interface IUsageProvider
{
    Task<UsageInfo> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Samples total system CPU/RAM. Stateful — CPU is a delta between samples.</summary>
public interface ISystemProvider
{
    /// <returns>(total CPU %, RAM used MB).</returns>
    (double CpuPercent, double RamUsedMb) Sample();
}

/// <summary>Samples the Claude/agent process CPU/RAM.</summary>
public interface IMetricsProvider
{
    /// <returns>(process CPU %, process RAM MB) summed across matched processes.</returns>
    (double CpuPercent, double RamMb) Sample();
}

/// <summary>Bounded time-series store feeding the history charts.</summary>
public interface IHistoryStore
{
    void Append(HistorySample sample);
    System.Collections.Generic.IReadOnlyList<HistorySample> Snapshot();
}

/// <summary>
/// Holds the last-known slice from each provider and merges them into a full
/// <see cref="HudSnapshot"/> with a fresh timestamp. Thread-safe.
/// </summary>
public interface ISnapshotAggregator
{
    HudSnapshot Current { get; }
    HudSnapshot ApplyClaude(ClaudeSnapshot claude);
    HudSnapshot ApplyCostTotals(CostTotals totals);
    HudSnapshot ApplyPerf(PerfInfo perf);
    HudSnapshot ApplyUsage(UsageInfo usage);
}

/// <summary>Delivers a user-facing notification (toast/banner). Phase 3 logs only.</summary>
public interface INotificationProvider
{
    void Notify(Alerts.Alert alert);
}
