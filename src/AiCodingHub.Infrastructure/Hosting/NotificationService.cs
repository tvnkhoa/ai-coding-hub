using System.Threading;
using System.Threading.Tasks;
using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Alerts;
using AiCodingHub.Core.Messaging;
using AiCodingHub.Core.Models;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;

namespace AiCodingHub.Infrastructure.Hosting;

/// <summary>
/// Subscribes to snapshots, runs the alert evaluator, and fans out raised alerts to the
/// messenger (for the UI) and the notification provider (toast/log).
/// </summary>
public sealed class NotificationService : IHostedService
{
    private readonly IMessenger _messenger;
    private readonly IAlertEvaluator _evaluator;
    private readonly INotificationProvider _notifications;
    private readonly TimeProvider _time;

    public NotificationService(
        IMessenger messenger,
        IAlertEvaluator evaluator,
        INotificationProvider notifications,
        TimeProvider time)
    {
        _messenger = messenger;
        _evaluator = evaluator;
        _notifications = notifications;
        _time = time;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _messenger.Register<NotificationService, SnapshotUpdatedMessage>(this, static (recipient, message) =>
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
        var alerts = _evaluator.Evaluate(snapshot, _time.GetUtcNow());
        foreach (var alert in alerts)
        {
            _messenger.Send(new AlertRaisedMessage(alert));
            _notifications.Notify(alert);
        }
    }
}
