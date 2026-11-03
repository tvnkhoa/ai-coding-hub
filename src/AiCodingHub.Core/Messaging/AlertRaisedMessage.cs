using AiCodingHub.Core.Alerts;

namespace AiCodingHub.Core.Messaging;

/// <summary>Published when the alert evaluator raises an alert (consumed by the UI/toasts).</summary>
public sealed record AlertRaisedMessage(Alert Alert);
