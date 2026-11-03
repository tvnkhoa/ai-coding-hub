using System;
using System.Threading;
using System.Threading.Tasks;
using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Messaging;
using AiCodingHub.Core.Options;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiCodingHub.Infrastructure.Hosting;

/// <summary>
/// Probes the subscription usage limits on a slow cadence (minutes) because each probe spends a
/// request against quota. When the probe is disabled the loop idles and re-checks options so it
/// starts working as soon as a token is added — no restart needed.
/// </summary>
public sealed class UsageCollector : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisabledPoll = TimeSpan.FromSeconds(60);

    private readonly IUsageProvider _usage;
    private readonly ISnapshotAggregator _aggregator;
    private readonly IMessenger _messenger;
    private readonly IOptionsMonitor<UsageOptions> _options;
    private readonly ILogger<UsageCollector> _log;

    // Released by the "refresh now" message so a wait collapses into an immediate probe.
    private readonly SemaphoreSlim _manual = new(0);

    public UsageCollector(
        IUsageProvider usage,
        ISnapshotAggregator aggregator,
        IMessenger messenger,
        IOptionsMonitor<UsageOptions> options,
        ILogger<UsageCollector> log)
    {
        _usage = usage;
        _aggregator = aggregator;
        _messenger = messenger;
        _options = options;
        _log = log;

        // Singleton hosted service, so a strong registration is safe for the app's lifetime.
        _messenger.Register<UsageCollector, RefreshUsageRequestedMessage>(this, static (r, _) => r.SignalManualRefresh());
    }

    private void SignalManualRefresh()
    {
        // Coalesce bursts: only hold at most one pending permit.
        if (_manual.CurrentCount == 0)
        {
            _manual.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(StartupDelay, stoppingToken).ConfigureAwait(false))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = _options.CurrentValue;
            TimeSpan next;

            if (opt.Enabled && opt.HasTokenSource)
            {
                try
                {
                    var info = await _usage.GetAsync(stoppingToken).ConfigureAwait(false);
                    var snapshot = _aggregator.ApplyUsage(info);
                    _messenger.Send(new SnapshotUpdatedMessage(snapshot));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Usage collection iteration failed");
                }

                next = TimeSpan.FromMinutes(Math.Max(1, opt.RefreshMinutes));
            }
            else
            {
                next = DisabledPoll;
            }

            if (!await WaitForNextAsync(next, stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Waits until the cadence elapses <b>or</b> a manual refresh is requested — whichever comes
    /// first. Returns false only on cancellation. Uses the semaphore's timeout so no waiter is
    /// leaked when the timer wins.
    /// </summary>
    private async Task<bool> WaitForNextAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await _manual.WaitAsync(delay, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
