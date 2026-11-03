using AiCodingHub.Core.Models;

namespace AiCodingHub.Core.Messaging;

/// <summary>
/// Published on the messenger whenever the aggregator produces a new snapshot. ViewModels
/// subscribe and project the payload onto the UI.
/// </summary>
public sealed record SnapshotUpdatedMessage(HudSnapshot Snapshot);
