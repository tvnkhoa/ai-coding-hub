using System;
using System.IO;
using AiCodingHub.Core.Alerts;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using AiCodingHub.Core.Pricing;
using AiCodingHub.Infrastructure.Claude;
using AiCodingHub.Infrastructure.History;
using AiCodingHub.Infrastructure.Hosting;
using AiCodingHub.Infrastructure.Notifications;
using Microsoft.Extensions.Time.Testing;
using TaskStatus = AiCodingHub.Core.Models.TaskStatus;

namespace AiCodingHub.Tests;

public class ClaudeSettingsReaderTests
{
    private static string WriteTempSettings(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aicodinghub-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Theory]
    [InlineData("\"opus[1m]\"", true)]
    [InlineData("\"claude-sonnet-5-1m\"", true)]
    [InlineData("\"opus\"", false)]
    [InlineData("\"claude-opus-4-8\"", false)]
    public void Detects_Large_Context_From_Model_Alias(string modelValue, bool expected)
    {
        var path = WriteTempSettings($"{{ \"model\": {modelValue}, \"theme\": \"dark\" }}");
        try
        {
            var reader = new ClaudeSettingsReader(path);
            Assert.Equal(expected, reader.IsLargeContext());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_File_Is_Not_Large_Context()
    {
        var reader = new ClaudeSettingsReader(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.json"));
        Assert.False(reader.IsLargeContext());
    }

    [Fact]
    public void Malformed_File_Does_Not_Throw()
    {
        var path = WriteTempSettings("{ not valid json ");
        try
        {
            var reader = new ClaudeSettingsReader(path);
            Assert.False(reader.IsLargeContext());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ModelCatalogTests
{
    private readonly IPricingCatalog _catalog = new ModelCatalog();

    [Theory]
    [InlineData("claude-opus-5-5", "opus", 1_000_000)]
    [InlineData("claude-opus-4-8", "opus", 1_000_000)]
    [InlineData("claude-opus-4-1-20250805", "opus", 200_000)]
    [InlineData("claude-sonnet-5", "sonnet", 1_000_000)]
    [InlineData("claude-sonnet-4-5-20250929", "sonnet", 200_000)]
    [InlineData("claude-sonnet-4-5[1m]", "sonnet", 1_000_000)]
    [InlineData("claude-haiku-4-5-20251001", "haiku", 200_000)]
    [InlineData("claude-haiku-5-5", "haiku", 1_000_000)]
    [InlineData("claude-fable-5", "fable", 1_000_000)]
    [InlineData("claude-fable-5-1", "fable", 1_000_000)]
    public void Resolves_Family_And_Window(string model, string family, long window)
    {
        var p = _catalog.Resolve(model);
        Assert.Equal(family, p.Family);
        Assert.Equal(window, p.ContextWindow);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5", 4.0, 20.0, 5.0, 8.0, 0.20)]
    [InlineData("claude-opus-5", "Opus 5", 5.0, 25.0, 6.25, 10.0, 0.50)]
    [InlineData("claude-opus-4-1-20250805", "Opus 4", 15.0, 75.0, 18.75, 30.0, 1.50)]
    [InlineData("claude-fable-5-1", "Fable 5.1", 10.0, 50.0, 12.5, 20.0, 0.25)]
    [InlineData("claude-fable-5", "Fable 5", 10.0, 50.0, 12.5, 20.0, 1.00)]
    [InlineData("claude-sonnet-5-5", "Sonnet 5.5", 2.0, 10.0, 2.5, 4.0, 0.20)]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5", 1.0, 5.0, 1.25, 2.0, 0.10)]
    public void Resolves_Current_List_Prices(string model, string label, double input, double output, double write5m, double write1h, double read)
    {
        var p = _catalog.Resolve(model);
        Assert.Equal(label, p.Label);
        Assert.Equal((decimal)input, p.InputPerMTok);
        Assert.Equal((decimal)output, p.OutputPerMTok);
        Assert.Equal((decimal)write5m, p.CacheWritePerMTok);
        Assert.Equal((decimal)write1h, p.CacheWrite1hPerMTok);
        Assert.Equal((decimal)read, p.CacheReadPerMTok);
    }

    [Fact]
    public void Unknown_Model_Falls_Back()
    {
        var p = _catalog.Resolve("gpt-4o");
        Assert.Equal("unknown", p.Family);
        Assert.Equal(200_000, p.ContextWindow);
    }
}

public class ClaudeCredentialsReaderTests
{
    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aicodinghub-creds-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Reads_Access_Token_And_Expiry()
    {
        var path = WriteTemp(
            "{ \"claudeAiOauth\": { \"accessToken\": \"sk-ant-oat01-abc\", \"expiresAt\": 1783936516320 }, \"organizationUuid\": \"x\" }");
        try
        {
            var (token, expiresAt) = new ClaudeCredentialsReader(path).Read();
            Assert.Equal("sk-ant-oat01-abc", token);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1783936516320), expiresAt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_File_Returns_Null()
    {
        var (token, expiresAt) = new ClaudeCredentialsReader(
            Path.Combine(Path.GetTempPath(), $"no-creds-{Guid.NewGuid():N}.json")).Read();
        Assert.Null(token);
        Assert.Null(expiresAt);
    }

    [Fact]
    public void Expands_Environment_Variables_In_Custom_Path()
    {
        var path = WriteTemp("{ \"claudeAiOauth\": { \"accessToken\": \"sk-ant-oat01-env\" } }");
        var varName = $"AICODINGHUB_CREDS_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(varName, path);
        try
        {
            var reader = new ClaudeCredentialsReader($"%{varName}%");
            Assert.True(reader.FileExists);
            Assert.Equal(path, reader.ResolvedPath);
            Assert.Equal("sk-ant-oat01-env", reader.Read().Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
            File.Delete(path);
        }
    }
}

public class UsageParsingTests
{
    private static Func<string, string?> Headers(params (string Key, string Value)[] pairs) =>
        name =>
        {
            foreach (var (k, v) in pairs)
            {
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                {
                    return v;
                }
            }
            return null;
        };

    [Fact]
    public void Parses_Unified_Headers_Into_Percent_And_Reset()
    {
        var info = AnthropicUsageProvider.ParseUnifiedHeaders(Headers(
            ("anthropic-ratelimit-unified-5h-utilization", "0.42"),
            ("anthropic-ratelimit-unified-5h-reset", "1783936516"),
            ("anthropic-ratelimit-unified-7d-utilization", "0.68"),
            ("anthropic-ratelimit-unified-7d-reset", "1784500000"),
            ("anthropic-ratelimit-unified-5h-status", "allowed")));

        Assert.True(info.Available);
        Assert.Equal(42, info.FiveHourPercent, 3);
        Assert.Equal(68, info.WeeklyPercent, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783936516), info.FiveHourResetAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784500000), info.WeeklyResetAt);
        Assert.Equal("allowed", info.Status);
    }

    [Fact]
    public void No_Unified_Headers_Is_Unavailable()
    {
        var info = AnthropicUsageProvider.ParseUnifiedHeaders(_ => null);
        Assert.False(info.Available);
        Assert.Same(UsageInfo.Empty, info);
    }

    [Fact]
    public void Partial_Headers_Still_Available_And_Clamped()
    {
        // Only the 5h window present; utilization above 1.0 clamps to 100%.
        var info = AnthropicUsageProvider.ParseUnifiedHeaders(Headers(
            ("anthropic-ratelimit-unified-5h-utilization", "1.5")));

        Assert.True(info.Available);
        Assert.Equal(100, info.FiveHourPercent, 3);
        Assert.Equal(0, info.WeeklyPercent, 3);
        Assert.Null(info.WeeklyResetAt);
    }
}

public class AlertEvaluatorTests
{
    private static HudSnapshot SnapshotWithContext(double pct, decimal cost = 0m, TaskState task = TaskState.Working)
    {
        var ctx = new ContextInfo((long)(pct / 100.0 * 200_000), 200_000);
        return HudSnapshot.Empty with
        {
            HasActiveSession = true,
            Context = ctx,
            Cost = new CostInfo(cost, 0, 0, 0),
            Task = new TaskStatus(task, DateTimeOffset.UnixEpoch),
        };
    }

    [Fact]
    public void Context_Alert_Is_Edge_Triggered_Then_Escalates()
    {
        var eval = new AlertEvaluator(new TestOptionsMonitor<AlertOptions>(new AlertOptions()));
        var now = DateTimeOffset.UnixEpoch;

        // First cross into warn (85%) -> one alert.
        var a1 = eval.Evaluate(SnapshotWithContext(85), now);
        Assert.Single(a1);
        Assert.Equal(AlertKind.ContextWarn, a1[0].Kind);

        // Still in warn band -> no repeat.
        var a2 = eval.Evaluate(SnapshotWithContext(86), now);
        Assert.Empty(a2);

        // Escalate to critical (96%) -> one new alert.
        var a3 = eval.Evaluate(SnapshotWithContext(96), now);
        Assert.Single(a3);
        Assert.Equal(AlertKind.ContextCritical, a3[0].Kind);
    }

    [Fact]
    public void Cost_Alert_Fires_Once_When_Threshold_Crossed()
    {
        var eval = new AlertEvaluator(new TestOptionsMonitor<AlertOptions>(new AlertOptions()));
        var now = DateTimeOffset.UnixEpoch;

        Assert.Empty(eval.Evaluate(SnapshotWithContext(10, cost: 4.99m), now));
        var fired = eval.Evaluate(SnapshotWithContext(10, cost: 5.00m), now);
        Assert.Contains(fired, a => a.Kind == AlertKind.CostExceeded);
        // No repeat while still over.
        Assert.DoesNotContain(eval.Evaluate(SnapshotWithContext(10, cost: 6.00m), now), a => a.Kind == AlertKind.CostExceeded);
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void Ring_Evicts_Oldest_Beyond_Capacity()
    {
        var store = new InMemoryRingHistoryStore(capacity: 2);
        store.Append(new HistorySample(DateTimeOffset.UnixEpoch, 10, 0, 0, 0, 1));
        store.Append(new HistorySample(DateTimeOffset.UnixEpoch.AddSeconds(1), 20, 0, 0, 0, 2));
        store.Append(new HistorySample(DateTimeOffset.UnixEpoch.AddSeconds(2), 30, 0, 0, 0, 3));

        var samples = store.Snapshot();
        Assert.Equal(2, samples.Count);
        Assert.Equal(20, samples[0].ContextPercent);
        Assert.Equal(30, samples[1].ContextPercent);
    }
}

public class SnapshotAggregatorTests
{
    [Fact]
    public void Merges_Slices_And_Keeps_Last_Known()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var agg = new SnapshotAggregator(time);

        agg.ApplyPerf(new PerfInfo(12, 800, 3, 120));
        var claude = ClaudeSnapshot.Empty with
        {
            HasActiveSession = true,
            Session = SessionInfo.Empty with { Model = "claude-sonnet-5", Workspace = "D:\\x" },
        };
        var merged = agg.ApplyClaude(claude);

        Assert.True(merged.HasActiveSession);
        Assert.Equal("claude-sonnet-5", merged.Session.Model);
        // perf slice retained from the earlier apply
        Assert.Equal(12, merged.Perf.TotalCpuPercent);
    }

    [Fact]
    public void Cost_Totals_Merge_Into_Cost_And_Groups_Pass_Through()
    {
        var agg = new SnapshotAggregator(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        agg.ApplyCostTotals(new CostTotals(1.5m, 7m, 20m));

        var group = new SessionGroup("repo", new[]
        {
            new SessionSummary("abc", "repo", SessionRole.Leader, "m", "M", "p", "", 10, 100, 0.5m, 1,
                null, null, null, false, SessionActivity.Active),
        });
        var merged = agg.ApplyClaude(ClaudeSnapshot.Empty with
        {
            HasActiveSession = true,
            Cost = new CostInfo(0.5m, 0, 0, 0),
            Groups = new[] { group },
        });

        Assert.Equal(0.5m, merged.Cost.SessionUsd);
        Assert.Equal(1.5m, merged.Cost.TodayUsd);
        Assert.Equal(7m, merged.Cost.WeekUsd);
        Assert.Equal(20m, merged.Cost.MonthUsd);
        Assert.Single(merged.Groups);
    }
}
