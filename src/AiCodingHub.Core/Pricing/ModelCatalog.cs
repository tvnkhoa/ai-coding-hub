using System;

namespace AiCodingHub.Core.Pricing;

/// <summary>
/// Per-model context window + token prices. Prices are USD per million tokens (MTok).
/// <para>
/// Cache writes are priced by TTL: <see cref="CacheWritePerMTok"/> is the 5-minute rate and
/// <see cref="CacheWrite1hPerMTok"/> the 1-hour rate (Claude Code writes 1-hour entries).
/// Some models (Haiku 5.5) charge a higher rate once the prompt exceeds
/// <see cref="LongPromptThreshold"/> tokens; every rate is then multiplied by
/// <see cref="LongPromptMultiplier"/>.
/// </para>
/// </summary>
public sealed record ModelPricing(
    string Family,
    string Label,
    long ContextWindow,
    decimal InputPerMTok,
    decimal OutputPerMTok,
    decimal CacheWritePerMTok,
    decimal CacheWrite1hPerMTok,
    decimal CacheReadPerMTok,
    long LongPromptThreshold = 0,
    decimal LongPromptMultiplier = 1m);

/// <summary>Resolves pricing/context-window for a model id string.</summary>
public interface IPricingCatalog
{
    ModelPricing Resolve(string? model);
}

/// <summary>
/// Built-in, data-driven pricing table. Rows are matched by model-id substring in order, so the
/// most specific id comes first (<c>opus-5-5</c> before <c>opus-5</c>). Prices are Anthropic
/// first-party list prices (claude-api reference, cached 2026-10-06); keep this table as data.
/// </summary>
public sealed class ModelCatalog : IPricingCatalog
{
    /// <summary>Context window of older models (and the fallback for unknown ids).</summary>
    public const long DefaultWindow = 200_000;

    /// <summary>1M-token window: native on current models, a beta on some older ones ("[1m]").</summary>
    public const long LargeWindow = 1_000_000;

    private static readonly (string[] Keys, ModelPricing Pricing)[] Rows =
    {
        (new[] { "fable-5-1", "mythos-5-1" }, new("fable", "Fable 5.1", LargeWindow, 10m, 50m, 12.5m, 20m, 0.25m)),
        (new[] { "fable", "mythos" },          new("fable", "Fable 5", LargeWindow, 10m, 50m, 12.5m, 20m, 1.00m)),
        (new[] { "opus-5-5" },                 new("opus", "Opus 5.5", LargeWindow, 4m, 20m, 5m, 8m, 0.20m)),
        (new[] { "opus-5" },                   new("opus", "Opus 5", LargeWindow, 5m, 25m, 6.25m, 10m, 0.50m)),
        (new[] { "opus-4-8" },                 new("opus", "Opus 4.8", LargeWindow, 5m, 25m, 6.25m, 10m, 0.50m)),
        (new[] { "opus-4-7" },                 new("opus", "Opus 4.7", LargeWindow, 5m, 25m, 6.25m, 10m, 0.50m)),
        (new[] { "opus-4-6" },                 new("opus", "Opus 4.6", LargeWindow, 5m, 25m, 6.25m, 10m, 0.50m)),
        (new[] { "opus-4-5" },                 new("opus", "Opus 4.5", DefaultWindow, 5m, 25m, 6.25m, 10m, 0.50m)),
        (new[] { "opus" },                     new("opus", "Opus 4", DefaultWindow, 15m, 75m, 18.75m, 30m, 1.50m)),
        (new[] { "sonnet-5-5" },               new("sonnet", "Sonnet 5.5", LargeWindow, 2m, 10m, 2.5m, 4m, 0.20m)),
        (new[] { "sonnet-5" },                 new("sonnet", "Sonnet 5", LargeWindow, 2m, 10m, 2.5m, 4m, 0.20m)),
        (new[] { "sonnet-4-6" },               new("sonnet", "Sonnet 4.6", LargeWindow, 3m, 15m, 3.75m, 6m, 0.30m)),
        (new[] { "sonnet" },                   new("sonnet", "Sonnet 4", DefaultWindow, 3m, 15m, 3.75m, 6m, 0.30m)),
        (new[] { "haiku-5-5" },                new("haiku", "Haiku 5.5", LargeWindow, 0.10m, 0.50m, 0.125m, 0.20m, 0.01m, 100_000, 5m)),
        (new[] { "haiku-4-5" },                new("haiku", "Haiku 4.5", DefaultWindow, 1m, 5m, 1.25m, 2m, 0.10m)),
        (new[] { "haiku" },                    new("haiku", "Haiku 3.5", DefaultWindow, 0.80m, 4m, 1m, 1.6m, 0.08m)),
    };

    /// <summary>Unknown ids are priced as the current Sonnet (a middle estimate) with a 200k window.</summary>
    public static ModelPricing Unknown { get; } =
        new("unknown", "Claude", DefaultWindow, 2m, 10m, 2.5m, 4m, 0.20m);

    public ModelPricing Resolve(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Unknown;
        }

        var id = model.ToLowerInvariant();
        var pricing = Unknown;
        foreach (var (keys, row) in Rows)
        {
            if (Array.Exists(keys, k => id.Contains(k, StringComparison.Ordinal)))
            {
                pricing = row;
                break;
            }
        }

        // The 1M-context beta on older models is tagged e.g. "claude-sonnet-4-5[1m]" or "...-1m".
        return IsLargeContextId(id) && pricing.ContextWindow < LargeWindow
            ? pricing with { ContextWindow = LargeWindow }
            : pricing;
    }

    /// <summary>True when a model id/alias carries the 1M marker (<c>[1m]</c> or <c>-1m</c>).</summary>
    public static bool IsLargeContextId(string id) =>
        id.Contains("[1m]", StringComparison.OrdinalIgnoreCase) || id.Contains("-1m", StringComparison.OrdinalIgnoreCase);
}
