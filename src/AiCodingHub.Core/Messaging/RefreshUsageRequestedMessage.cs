namespace AiCodingHub.Core.Messaging;

/// <summary>
/// Requests an immediate, out-of-cycle usage-limit probe (the "refresh now" button), independent
/// of the timed cadence. Consumed by the usage collector.
/// </summary>
public sealed record RefreshUsageRequestedMessage;
