using System;
using System.Threading;
using System.Threading.Tasks;
using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Messaging;
using AiCodingHub.Core.Options;
using AiCodingHub.Core.Pricing;
using AiCodingHub.Infrastructure.Claude;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiCodingHub.Infrastructure.Hosting;

/// <summary>
/// Refreshes today / week / month spend on its own loop, so a scan never delays the session
/// poll. The <see cref="CostLedger"/> keeps per-file offsets, so after the first pass each scan
/// reads only bytes appended since the last one.
/// </summary>
public sealed class CostScanService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly CostLedger _ledger = new();
    private readonly IPricingCatalog _pricing;
    private readonly ISnapshotAggregator _aggregator;
    private readonly IMessenger _messenger;
    private readonly IOptionsMonitor<HudOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CostScanService> _log;

    public CostScanService(
        IPricingCatalog pricing,
        ISnapshotAggregator aggregator,
        IMessenger messenger,
        IOptionsMonitor<HudOptions> options,
        TimeProvider time,
        ILogger<CostScanService> log)
    {
        _pricing = pricing;
        _aggregator = aggregator;
        _messenger = messenger;
        _options = options;
        _time = time;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var opts = _options.CurrentValue;
                    var root = string.IsNullOrWhiteSpace(opts.ClaudeProjectsRoot) ? SessionLocator.DefaultRoot : opts.ClaudeProjectsRoot!;
                    var started = _time.GetTimestamp();
                    // File I/O off the host thread; the ledger is only touched by this loop.
                    var totals = await Task.Run(() => _ledger.Scan(root, _pricing, _time.GetUtcNow(), opts.DayStartHour), stoppingToken)
                        .ConfigureAwait(false);
                    _log.LogDebug("Cost scan: {Files} files, {Bytes} new bytes in {Ms} ms",
                        _ledger.TrackedFiles, _ledger.LastScanBytes, _time.GetElapsedTime(started).TotalMilliseconds);

                    var snapshot = _aggregator.ApplyCostTotals(totals);
                    _messenger.Send(new SnapshotUpdatedMessage(snapshot));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Cost scan failed");
                }

                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
