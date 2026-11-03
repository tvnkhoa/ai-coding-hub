using System;
using Microsoft.Extensions.Options;

namespace AiCodingHub.Tests;

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a fixed value for unit tests.</summary>
public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
