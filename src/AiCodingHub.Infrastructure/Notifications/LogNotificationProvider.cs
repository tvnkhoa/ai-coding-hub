using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Alerts;
using Microsoft.Extensions.Logging;

namespace AiCodingHub.Infrastructure.Notifications;

/// <summary>
/// Phase 3 notification sink: writes alerts to the log. Replaced by a Windows toast provider
/// in Phase 6 behind the same <see cref="INotificationProvider"/> interface.
/// </summary>
public sealed class LogNotificationProvider : INotificationProvider
{
    private readonly ILogger<LogNotificationProvider> _log;

    public LogNotificationProvider(ILogger<LogNotificationProvider> log) => _log = log;

    public void Notify(Alert alert) =>
        _log.LogInformation("ALERT [{Severity}] {Title}: {Message}",
            alert.Severity, alert.Title, alert.Message);
}
