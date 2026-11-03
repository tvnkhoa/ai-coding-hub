using System;
using System.Threading;
using System.Threading.Tasks;
using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Messaging;
using AiCodingHub.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;

namespace AiCodingHub.Infrastructure.Hosting;

/// <summary>
/// Subscribes to snapshots and appends a downsampled <see cref="HistorySample"/> (≤ 1 Hz) to
/// the history store for the charts.
/// </summary>
public sealed class HistoryRecorder : IHostedService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly IMessenger _messenger;
    private readonly IHistoryStore _store;
    private readonly TimeProvider _time;
    private DateTimeOffset _lastSample = DateTimeOffset.MinValue;

    public HistoryRecorder(IMessenger messenger, IHistoryStore store, TimeProvider time)
    {
        _messenger = messenger;
        _store = store;
        _time = time;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _messenger.Register<HistoryRecorder, SnapshotUpdatedMessage>(this, static (recipient, message) =>
            recipient.OnSnapshot(message.Snapshot));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _messenger.UnregisterAll(this);
        return Task.CompletedTask;
    }

    private void OnSnapshot(HudSnapshot snapshot)
    {
        var now = _time.GetUtcNow();
        if (now - _lastSample < MinInterval)
        {
            return;
        }
        _lastSample = now;

        _store.Append(new HistorySample(
            At: now,
            ContextPercent: snapshot.Context.Percentage,
            SessionCostUsd: snapshot.Cost.SessionUsd,
            TotalCpuPercent: snapshot.Perf.TotalCpuPercent,
            TotalRamMb: snapshot.Perf.TotalRamMb,
            Requests: snapshot.Requests.Requests));
    }
}
