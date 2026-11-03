using System;
using System.Linq;
using AiCodingHub.Core.Alerts;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using AiCodingHub.Core.Pricing;
using AiCodingHub.Infrastructure.Claude;
using AiCodingHub.Infrastructure.Notifications;

namespace AiCodingHub.Tests;

/// <summary>Session names and "waiting for the owner" detection, from inline fixtures.</summary>
public class DecisionAndNameTests
{
    private static readonly IPricingCatalog Pricing = new ModelCatalog();
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-03T04:00:00Z");

    private const string AskBlock =
        """{"type":"tool_use","id":"toolu_ask1","name":"AskUserQuestion","input":{"questions":[{"question":"Ship behind a flag?","header":"Release","options":[]}]}}""";

    private static string Answer(string toolUseId, DateTimeOffset at) =>
        $$$"""{"type":"user","timestamp":"{{{at:O}}}","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"{{{toolUseId}}}","content":"ok"}]}}""";

    [Fact]
    public void Reads_The_Launcher_Name_From_Agent_Name_Records()
    {
        var events = Fx.Parse(new[]
        {
            """{"type":"custom-title","customTitle":"old-title","sessionId":"s"}""",
            Fx.Assistant(T0, "claude-opus-5-5", "m1", "r1"),
            """{"type":"agent-name","agentName":"aria-spec094-build","sessionId":"s"}""",
        });

        var snap = TranscriptDeriver.Derive(events, Pricing, TimeSpan.FromMinutes(5), T0);

        Assert.Equal("aria-spec094-build", snap.Name);
    }

    [Fact]
    public void An_Unanswered_AskUserQuestion_Is_A_Pending_Decision_Until_Answered()
    {
        var asked = Fx.Assistant(T0, "claude-opus-5-5", "m1", "r1", block: AskBlock);

        var pending = TranscriptDeriver.Derive(Fx.Parse(new[] { asked }), Pricing, TimeSpan.FromMinutes(5), T0.AddMinutes(4));
        var d = Assert.IsType<DecisionRequest>(pending.PendingDecision);
        Assert.Equal("toolu_ask1", d.ToolUseId);
        Assert.Equal("Release", d.Header);
        Assert.Equal("Ship behind a flag?", d.Question);
        Assert.Equal(T0, d.Since);

        var answered = TranscriptDeriver.Derive(Fx.Parse(new[] { asked, Answer("toolu_ask1", T0.AddMinutes(5)) }),
            Pricing, TimeSpan.FromMinutes(5), T0.AddMinutes(6));
        Assert.Null(answered.PendingDecision);
    }

    [Fact]
    public void ExitPlanMode_Waits_For_Plan_Approval()
    {
        var plan = Fx.Assistant(T0, "claude-opus-5-5", "m1", "r1",
            block: """{"type":"tool_use","id":"toolu_plan","name":"ExitPlanMode","input":{}}""");

        var snap = TranscriptDeriver.Derive(Fx.Parse(new[] { plan }), Pricing, TimeSpan.FromMinutes(5), T0);

        Assert.Equal("plan", snap.PendingDecision?.Kind);
    }

    [Fact]
    public void Request_Replay_Records_Do_Not_Count_As_Questions()
    {
        // api-request-blob lines replay old messages (tool_use included); they are not live turns.
        var blob = $$$"""{"type":"api-request-blob","sessionId":"s","message":{"role":"assistant","content":[{{{AskBlock}}}]}}""";

        var snap = TranscriptDeriver.Derive(Fx.Parse(new[] { Fx.User(T0), blob }), Pricing, TimeSpan.FromMinutes(5), T0);

        Assert.Null(snap.PendingDecision);
    }

    [Theory]
    [InlineData("aria-explainers", SessionRole.Member, "aria-explainers")]
    [InlineData(null, SessionRole.Leader, "Leader")]
    [InlineData(null, SessionRole.Member, "abcdef12")]
    public void Display_Name_Falls_Back_To_Leader_Then_Short_Id(string? name, SessionRole role, string expected)
    {
        var s = new SessionSummary("abcdef12-3456", "repo", role, "m", "M", "p", "", 0, 1, 0, 0,
            null, null, null, false, SessionActivity.Active, Name: name);

        Assert.Equal(expected, s.DisplayName);
    }

    [Fact]
    public void Decision_Alert_Fires_Once_Per_Question()
    {
        static HudSnapshot Snap(string? toolUseId) => HudSnapshot.Empty with
        {
            HasActiveSession = true,
            Groups = new[]
            {
                new SessionGroup("wec.be", new[]
                {
                    new SessionSummary("4d4ddc08-aaaa", "wec.be", SessionRole.Leader, "m", "M", "p", "", 0, 1, 0, 0,
                        null, null, 1, false, SessionActivity.Active,
                        Decision: toolUseId is null ? null : new DecisionRequest(toolUseId, "question", "Release", "?", T0)),
                }),
            },
        };

        var eval = new AlertEvaluator(new TestOptionsMonitor<AlertOptions>(new AlertOptions { EnableIdle = false }));

        var first = eval.Evaluate(Snap("q1"), T0).Where(a => a.Kind == AlertKind.DecisionNeeded).ToList();
        var alert = Assert.Single(first);
        Assert.Equal("wec.be · Leader needs your decision", alert.Title);

        Assert.DoesNotContain(eval.Evaluate(Snap("q1"), T0), a => a.Kind == AlertKind.DecisionNeeded);
        Assert.DoesNotContain(eval.Evaluate(Snap(null), T0), a => a.Kind == AlertKind.DecisionNeeded);
        Assert.Contains(eval.Evaluate(Snap("q2"), T0), a => a.Kind == AlertKind.DecisionNeeded);
    }
}
