using System.Collections.Generic;
using AiCodingHub.Core.Abstractions;
using AiCodingHub.Core.Models;

namespace AiCodingHub.Infrastructure.History;

/// <summary>
/// Bounded, thread-safe ring buffer of history samples (default ~1 hour at 1 Hz). v1.0 may
/// swap in a disk/SQLite-backed store behind the same interface.
/// </summary>
public sealed class InMemoryRingHistoryStore : IHistoryStore
{
    private readonly int _capacity;
    private readonly Queue<HistorySample> _samples;
    private readonly object _gate = new();

    public InMemoryRingHistoryStore(int capacity = 3600)
    {
        _capacity = capacity < 1 ? 1 : capacity;
        _samples = new Queue<HistorySample>(_capacity);
    }

    public void Append(HistorySample sample)
    {
        lock (_gate)
        {
            if (_samples.Count >= _capacity)
            {
                _samples.Dequeue();
            }
            _samples.Enqueue(sample);
        }
    }

    public IReadOnlyList<HistorySample> Snapshot()
    {
        lock (_gate)
        {
            return _samples.ToArray();
        }
    }
}
