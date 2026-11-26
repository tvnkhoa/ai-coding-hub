using System;
using System.Collections.Generic;
using System.IO;
using AiCodingHub.Core.Pricing;
using AiCodingHub.Infrastructure.Claude;

namespace AiCodingHub.Tests;

/// <summary>
/// Reasoning effort per session: Claude Code stamps every assistant line with a top-level
/// <c>effort</c> (and <c>perTurnEffort</c>). Inline fixtures only.
/// </summary>
public class SessionEffortTests
{
    private static readonly IPricingCatalog Pricing = new ModelCatalog();
    private static readonly TranscriptParser Parser = new();
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-26T10:00:10Z");

    private static List<TranscriptEvent> Parse(params string[] lines)
    {
        var list = new List<TranscriptEvent>();
        foreach (var l in lines)
        {
            if (Parser.ParseLine(l) is { } ev) list.Add(ev);
        }
        return list;
    }

    private static string Assistant(string id, string ts, string effortFields) =>
        $$$"""{"type":"assistant","timestamp":"{{{ts}}}","requestId":"req_{{{id}}}",{{{effortFields}}}"message":{"id":"msg_{{{id}}}","role":"assistant","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"content":[{"type":"text","text":"ok"}]}}""";

    private const string User = """{"type":"user","timestamp":"2026-11-26T10:00:00Z","cwd":"D:\\repo","message":{"role":"user","content":"hi"}}""";

    [Fact]
    public void Parser_Reads_TopLevel_Effort()
    {
        var ev = Parser.ParseLine(Assistant("a", "2026-11-26T10:00:01Z", "\"effort\":\"high\",\"perTurnEffort\":\"high\","));
        Assert.Equal("high", ev!.Effort);
    }

    [Fact]
    public void Parser_Falls_Back_To_PerTurnEffort()
    {
        var ev = Parser.ParseLine(Assistant("a", "2026-11-26T10:00:01Z", "\"perTurnEffort\":\"low\","));
        Assert.Equal("low", ev!.Effort);
    }

    [Fact]
    public void Latest_Assistant_Effort_Wins()
    {
        var events = Parse(
            User,
            Assistant("a", "2026-11-26T10:00:01Z", "\"effort\":\"medium\",\"perTurnEffort\":\"medium\","),
            Assistant("b", "2026-11-26T10:00:05Z", "\"effort\":\"xhigh\",\"perTurnEffort\":\"xhigh\","));

        var snap = TranscriptDeriver.Derive(events, Pricing, TimeSpan.FromMinutes(5), Now);

        Assert.Equal("xhigh", snap.Session.Effort);
    }

    [Fact]
    public void Line_Without_Effort_Keeps_The_Last_Known()
    {
        var events = Parse(
            User,
            Assistant("a", "2026-11-26T10:00:01Z", "\"effort\":\"max\","),
            Assistant("b", "2026-11-26T10:00:05Z", ""),
            """{"type":"user","timestamp":"2026-11-26T10:00:06Z","effort":"low","message":{"role":"user","content":"x"}}""");

        var snap = TranscriptDeriver.Derive(events, Pricing, TimeSpan.FromMinutes(5), Now);

        Assert.Equal("max", snap.Session.Effort); // only assistant lines set it
    }

    [Fact]
    public void No_Assistant_Line_Means_No_Effort()
    {
        var snap = TranscriptDeriver.Derive(Parse(User), Pricing, TimeSpan.FromMinutes(5), Now);
        Assert.Null(snap.Session.Effort);
    }

    [Fact]
    public void Settings_EffortLevel_Is_The_Default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hud-effort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, """{"model":"opus","effortLevel":"High"}""");
            Assert.Equal("high", new ClaudeSettingsReader(path).EffortLevel());

            File.WriteAllText(path, """{"model":"opus"}""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Null(new ClaudeSettingsReader(path).EffortLevel());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
