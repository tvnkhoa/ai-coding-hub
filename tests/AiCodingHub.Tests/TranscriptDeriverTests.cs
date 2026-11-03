using System;
using System.Collections.Generic;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Pricing;
using AiCodingHub.Infrastructure.Claude;

namespace AiCodingHub.Tests;

/// <summary>
/// Derivation tests over hand-authored JSONL fixtures (never the user's real transcripts).
/// </summary>
public class TranscriptDeriverTests
{
    private static readonly IPricingCatalog Pricing = new ModelCatalog();
    private static readonly TranscriptParser Parser = new();

    private static List<TranscriptEvent> Parse(params string[] lines)
    {
        var list = new List<TranscriptEvent>();
        foreach (var l in lines)
        {
            var ev = Parser.ParseLine(l);
            if (ev is not null) list.Add(ev);
        }
        return list;
    }

    [Fact]
    public void Derives_Model_Context_And_Cost_From_Assistant_Usage()
    {
        var user = """
        {"type":"user","timestamp":"2026-07-13T10:00:00Z","cwd":"D:\\Projects\\GoogleAds","gitBranch":"feature/oauth","version":"1.2.3","message":{"role":"user","content":"hi"}}
        """;
        var assistant = """
        {"type":"assistant","timestamp":"2026-07-13T10:00:03Z","message":{"role":"assistant","model":"claude-sonnet-5","usage":{"input_tokens":1000,"output_tokens":500,"cache_creation_input_tokens":200,"cache_read_input_tokens":8000},"content":[{"type":"text","text":"hello"}]}}
        """;

        var events = Parse(user, assistant);
        var now = DateTimeOffset.Parse("2026-07-13T10:00:05Z");

        var snap = TranscriptDeriver.Derive(events, Pricing, TimeSpan.FromMinutes(5), now);

        Assert.True(snap.HasActiveSession);
        Assert.Equal("claude-sonnet-5", snap.Session.Model);
        Assert.Equal("GoogleAds", snap.Session.ProjectName);
        Assert.Equal("feature/oauth", snap.Session.Branch);

        // context = input + cache_read + cache_creation = 1000 + 8000 + 200
        Assert.Equal(9200, snap.Context.CurrentTokens);
        Assert.Equal(1_000_000, snap.Context.MaxTokens); // Sonnet 5 has a native 1M window

        // sonnet 5: in 2, out 10, cacheWrite(5m) 2.5, cacheRead 0.20 per MTok
        // (1000*2 + 500*10 + 200*2.5 + 8000*0.20) / 1e6 = (2000+5000+500+1600)/1e6 = 0.0091
        Assert.Equal(0.0091m, snap.Cost.SessionUsd);

        Assert.Equal(1, snap.Requests.Requests);
        Assert.Equal(1, snap.Requests.Prompts);
        Assert.Equal(3.0, snap.Requests.AvgLatencySeconds, 3);
        Assert.Equal(TaskState.Generating, snap.Task.State);
    }

    [Fact]
    public void Maps_ToolUse_To_Task_State()
    {
        var line = """
        {"type":"assistant","timestamp":"2026-07-13T10:00:03Z","message":{"role":"assistant","model":"claude-opus-4-8","content":[{"type":"tool_use","name":"Read","id":"x","input":{}}]}}
        """;
        var events = Parse(line);
        var now = DateTimeOffset.Parse("2026-07-13T10:00:04Z");

        var snap = TranscriptDeriver.Derive(events, Pricing, TimeSpan.FromMinutes(5), now);

        Assert.Equal(TaskState.ReadingFile, snap.Task.State);
        Assert.Equal(1, snap.Requests.ToolCalls);
    }

    [Fact]
    public void Reports_Idle_When_Last_Activity_Exceeds_Threshold()
    {
        var line = """
        {"type":"assistant","timestamp":"2026-07-13T10:00:03Z","message":{"role":"assistant","model":"claude-sonnet-5","content":[{"type":"text","text":"done"}]}}
        """;
        var events = Parse(line);
        var now = DateTimeOffset.Parse("2026-07-13T10:10:00Z"); // ~10 min later

        var snap = TranscriptDeriver.Derive(events, Pricing, TimeSpan.FromMinutes(5), now);

        Assert.Equal(TaskState.Idle, snap.Task.State);
    }

    [Fact]
    public void Empty_Events_Yield_Empty_Snapshot()
    {
        var snap = TranscriptDeriver.Derive(new List<TranscriptEvent>(), Pricing, TimeSpan.FromMinutes(5),
            DateTimeOffset.Parse("2026-07-13T10:00:00Z"));

        Assert.False(snap.HasActiveSession);
        Assert.Same(ClaudeSnapshot.Empty, snap);
    }
}
