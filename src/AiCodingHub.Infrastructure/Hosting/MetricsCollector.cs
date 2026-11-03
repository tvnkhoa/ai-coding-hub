using System;
using System.Threading;
using System.Threading.Tasks;
using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Messaging;
using AiCodingHub.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiCodingHub.Infrastructure.Hosting;

/// <summary>Samples system + Claude-process CPU/RAM on a fixed 1 s cadence and publishes.</summary>
public sealed class MetricsCollector : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ISystemProvider _system;
    private readonly IMetricsProvider _process;
    private readonly ISnapshotAggregator _aggregator;
    private readonly IMessenger _messenger;
    private readonly ILogger<MetricsCollector> _log;

    public MetricsCollector(
        ISystemProvider system,
        IMetricsProvider process,
        ISnapshotAggregator aggregator,
        IMessenger messenger,
        ILogger<MetricsCollector> log)
    {
        _system = system;
        _process = process;
        _aggregator = aggregator;
        _messenger = messenger;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var (cpu, ramUsed) = _system.Sample();
                var (procCpu, procRam) = _process.Sample();
                var perf = new PerfInfo(cpu, ramUsed, procCpu, procRam);
                var snapshot = _aggregator.ApplyPerf(perf);
                _messenger.Send(new SnapshotUpdatedMessage(snapshot));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Metrics sample failed");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
