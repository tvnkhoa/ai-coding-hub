using AiCodingHub.Core.Pricing;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>Maps token usage × model pricing to a USD cost (prices are per MTok).</summary>
public static class TokenCostCalculator
{
    public static decimal Cost(UsageTokens usage, ModelPricing pricing)
    {
        var raw = usage.Input * pricing.InputPerMTok
                  + usage.Output * pricing.OutputPerMTok
                  + usage.CacheCreation5m * pricing.CacheWritePerMTok
                  + usage.CacheCreation1h * pricing.CacheWrite1hPerMTok
                  + usage.CacheRead * pricing.CacheReadPerMTok;

        // Tiered models (Haiku 5.5) bill the whole request at the long-prompt rate past the threshold.
        if (pricing.LongPromptThreshold > 0 && usage.ContextTokens > pricing.LongPromptThreshold)
        {
            raw *= pricing.LongPromptMultiplier;
        }

        return raw / 1_000_000m;
    }
}
