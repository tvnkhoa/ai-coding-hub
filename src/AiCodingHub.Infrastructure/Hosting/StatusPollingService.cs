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
/// Primary refresh loop: polls the Claude provider at the configured cadence, merges the
/// slice into the aggregator, and publishes the snapshot. Interval hot-reloads from options.
/// </summary>
public sealed class StatusPollingService : BackgroundService
{
    private readonly IClaudeProvider _claude;
    private readonly ISnapshotAggregator _aggregator;
    private readonly IMessenger _messenger;
    private readonly IOptionsMonitor<HudOptions> _options;
    private readonly ILogger<StatusPollingService> _log;

    public StatusPollingService(
        IClaudeProvider claude,
        ISnapshotAggregator aggregator,
        IMessenger messenger,
        IOptionsMonitor<HudOptions> options,
        ILogger<StatusPollingService> log)
    {
        _claude = claude;
        _aggregator = aggregator;
        _messenger = messenger;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var slice = await _claude.GetAsync(stoppingToken).ConfigureAwait(false);
                var snapshot = _aggregator.ApplyClaude(slice);
                _messenger.Send(new SnapshotUpdatedMessage(snapshot));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Status poll iteration failed");
            }

            var interval = Math.Clamp(_options.CurrentValue.RefreshIntervalMs, 100, 5000);
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
