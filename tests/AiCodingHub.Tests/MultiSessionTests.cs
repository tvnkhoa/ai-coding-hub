using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiCodingHub.Core.Alerts;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Options;
using AiCodingHub.Core.Pricing;
using AiCodingHub.Infrastructure.Claude;
using AiCodingHub.Infrastructure.Notifications;
using AiCodingHub.Infrastructure.System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiCodingHub.Tests;

/// <summary>Inline JSONL fixture builders (tests never read the user's real transcripts).</summary>
internal static class Fx
{
    public static string User(DateTimeOffset at, string cwd = @"D:\\Work\\alpha", string text = "hi") =>
        $$$"""{"type":"user","timestamp":"{{{at:O}}}","cwd":"{{{cwd}}}","message":{"role":"user","content":"{{{text}}}"}}""";

    public static string Assistant(
        DateTimeOffset at,
        string model,
        string msgId,
        string requestId,
        long input = 1000,
        long output = 500,
        long cacheCreation = 0,
        long cache1h = 0,
        long cacheRead = 0,
        string block = """{"type":"text","text":"ok"}""",
        string cwd = @"D:\\Work\\alpha") =>
        $$$"""{"type":"assistant","timestamp":"{{{at:O}}}","cwd":"{{{cwd}}}","requestId":"{{{requestId}}}","message":{"id":"{{{msgId}}}","role":"assistant","model":"{{{model}}}","usage":{"input_tokens":{{{input}}},"output_tokens":{{{output}}},"cache_creation_input_tokens":{{{cacheCreation}}},"cache_read_input_tokens":{{{cacheRead}}},"cache_creation":{"ephemeral_5m_input_tokens":{{{cacheCreation - cache1h}}},"ephemeral_1h_input_tokens":{{{cache1h}}}}},"content":[{{{block}}}]}}""";

    public static List<TranscriptEvent> Parse(IEnumerable<string> lines)
    {
        var parser = new TranscriptParser();
        return lines.Select(parser.ParseLine).Where(e => e is not null).Select(e => e!).ToList();
    }

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hud-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public class CostAccuracyTests
{
    private static readonly IPricingCatalog Pricing = new ModelCatalog();
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-03T02:00:00Z");

    [Fact]
    public void Repeated_Content_Block_Lines_Count_Usage_Once()
    {
        // Claude Code writes one line per content block; each repeats the response's usage.
        var lines = new[]
        {
            Fx.User(T0),
            Fx.Assistant(T0.AddSeconds(2), "claude-opus-5-5", "msg_1", "req_1", block: """{"type":"thinking","thinking":""}"""),
            Fx.Assistant(T0.AddSeconds(2), "claude-opus-5-5", "msg_1", "req_1", block: """{"type":"tool_use","name":"Read","id":"a","input":{}}"""),
            Fx.Assistant(T0.AddSeconds(2), "claude-opus-5-5", "msg_1", "req_1", block: """{"type":"tool_use","name":"Grep","id":"b","input":{}}"""),
        };

        var snap = TranscriptDeriver.Derive(Fx.Parse(lines), Pricing, TimeSpan.FromMinutes(5), T0.AddSeconds(3));

        Assert.Equal(1, snap.Requests.Requests);
        Assert.Equal(2, snap.Requests.ToolCalls);
        // opus 5.5: (1000*4 + 500*20) / 1e6 — once, not three times
        Assert.Equal(0.014m, snap.Cost.SessionUsd);
    }

    [Fact]
    public void One_Hour_Cache_Writes_Use_The_One_Hour_Rate()
    {
        var lines = new[]
        {
            Fx.Assistant(T0, "claude-opus-5-5", "m", "r", input: 1000, output: 500, cacheCreation: 3000, cache1h: 2000, cacheRead: 10000),
        };

        var snap = TranscriptDeriver.Derive(Fx.Parse(lines), Pricing, TimeSpan.FromMinutes(5), T0);

        // 1000*4 + 500*20 + 1000(5m)*5 + 2000(1h)*8 + 10000*0.20 = 4000+10000+5000+16000+2000 = 37000
        Assert.Equal(0.037m, snap.Cost.SessionUsd);
        Assert.Equal(14000, snap.Context.CurrentTokens);
    }

    [Fact]
    public void Haiku_5_5_Bills_The_Long_Prompt_Tier_Above_100k()
    {
        var shortPrompt = TokenCostCalculator.Cost(new UsageTokens(100_000, 1000, 0, 0), Pricing.Resolve("claude-haiku-5-5"));
        var longPrompt = TokenCostCalculator.Cost(new UsageTokens(150_000, 1000, 0, 0), Pricing.Resolve("claude-haiku-5-5"));

        Assert.Equal((100_000 * 0.10m + 1000 * 0.50m) / 1_000_000m, shortPrompt);
        Assert.Equal((150_000 * 0.50m + 1000 * 2.50m) / 1_000_000m, longPrompt);
    }

    [Fact]
    public void Incremental_Fold_Matches_Full_Derivation()
    {
        var lines = new List<string> { Fx.User(T0) };
        for (var i = 0; i < 20; i++)
        {
            var at = T0.AddSeconds(10 * i + 3);
            lines.Add(Fx.Assistant(at, i % 2 == 0 ? "claude-opus-5-5" : "claude-sonnet-5-5", $"m{i}", $"r{i}", input: 100 * i, cacheRead: 1000 * i));
            lines.Add(Fx.Assistant(at, i % 2 == 0 ? "claude-opus-5-5" : "claude-sonnet-5-5", $"m{i}", $"r{i}", input: 100 * i, cacheRead: 1000 * i));
            lines.Add(Fx.User(at.AddSeconds(4)));
        }

        var events = Fx.Parse(lines);
        var now = T0.AddMinutes(10);
        var full = TranscriptDeriver.Derive(events, Pricing, TimeSpan.FromMinutes(5), now);

        var acc = new SessionAccumulator(Pricing);
        acc.AddRange(events.Take(17));
        acc.AddRange(events.Skip(17));
        var incremental = acc.ToSnapshot(TimeSpan.FromMinutes(5), now);

        Assert.Equal(full.Cost, incremental.Cost);
        Assert.Equal(full.Requests, incremental.Requests);
        Assert.Equal(full.Context, incremental.Context);
        Assert.Equal(full.Session, incremental.Session);
        Assert.Equal(20, incremental.Requests.Requests);
    }
}

public class JsonlTailerTests
{
    [Fact]
    public void Reads_Backlog_In_Bounded_Chunks_And_Waits_For_Partial_Lines()
    {
        var dir = Fx.TempDir();
        var path = Path.Combine(dir, "s.jsonl");
        try
        {
            var lines = Enumerable.Range(0, 50).Select(i => $$"""{"n":{{i}},"pad":"{{new string('x', 100)}}"}""").ToList();
            File.WriteAllText(path, string.Join("\n", lines) + "\n{\"partial\":");

            var tailer = new JsonlTailer(path);
            var got = new List<string>();
            var reads = 0;
            do
            {
                got.AddRange(tailer.ReadNewLines(maxBytes: 1024));
                reads++;
            }
            while (!tailer.IsAtEnd && reads < 100);

            Assert.True(reads > 1, "a 1 KB budget must take several reads");
            Assert.Equal(lines, got);

            // The partial line is returned once its newline is written.
            File.AppendAllText(path, "1}\n");
            Assert.Equal(new[] { "{\"partial\":1}" }, tailer.ReadNewLines());
            Assert.Empty(tailer.ReadNewLines());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_Line_Longer_Than_The_Budget_Is_Still_Read_Whole()
    {
        var dir = Fx.TempDir();
        var path = Path.Combine(dir, "s.jsonl");
        try
        {
            var big = $$"""{"big":"{{new string('y', 10_000)}}"}""";
            File.WriteAllText(path, big + "\n{\"n\":2}\n");

            var tailer = new JsonlTailer(path);
            Assert.Equal(new[] { big }, tailer.ReadNewLines(maxBytes: 512));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class CostLedgerTests
{
    [Fact]
    public void Buckets_Today_Dedupes_And_Reads_Only_Appended_Bytes()
    {
        var root = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var project = Directory.CreateDirectory(Path.Combine(root, "D--Work-alpha")).FullName;
            var file = Path.Combine(project, "s1.jsonl");
            var line = Fx.Assistant(now.AddMinutes(-1), "claude-opus-5-5", "m1", "r1");
            File.WriteAllText(file, line + "\n" + line + "\n"); // same response twice

            var ledger = new CostLedger();
            var first = ledger.Scan(root, new ModelCatalog(), now);
            Assert.Equal(0.014m, first.TodayUsd);
            Assert.Equal(0.014m, first.MonthUsd);

            var appended = Fx.Assistant(now, "claude-opus-5-5", "m2", "r2") + "\n";
            File.AppendAllText(file, appended);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));

            var second = ledger.Scan(root, new ModelCatalog(), now);
            Assert.Equal(0.028m, second.TodayUsd);
            Assert.Equal(Encoding.UTF8.GetByteCount(appended), ledger.LastScanBytes);

            // Nothing new: nothing read.
            ledger.Scan(root, new ModelCatalog(), now);
            Assert.Equal(0, ledger.LastScanBytes);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Day_Start_Hour_Moves_Early_Morning_Spend_To_The_Previous_Day()
    {
        var local = new DateTime(2026, 11, 3, 3, 30, 0, DateTimeKind.Unspecified);
        var at = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));

        Assert.Equal(new DateOnly(2026, 11, 3), CostLedger.WorkingDate(at, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 11, 2), CostLedger.WorkingDate(at, TimeSpan.FromHours(4)));
    }
}

public class HandoverRegistryTests
{
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    [Fact]
    public void Assigns_Leader_And_Members_For_Todays_Working_Day()
    {
        var dir = Fx.TempDir();
        try
        {
            File.WriteAllLines(Path.Combine(dir, ".sessions.jsonl"), new[]
            {
                """{"session":"old-1","repo":"alpha","day":"2026-11-02","source":"startup","at":"2026-11-02T01:00:00Z","pid":10}""",
                """{"session":"lead-1","repo":"alpha","day":"2026-11-03","source":"startup","at":"2026-11-03T01:00:00Z","pid":11}""",
                """{"session":"mem-1","repo":"alpha","day":"2026-11-03","source":"startup","at":"2026-11-03T01:05:00Z","pid":12}""",
                """{"session":"mem-1","repo":"alpha","day":"2026-11-03","source":"resume","at":"2026-11-03T02:00:00Z","pid":13}""",
                """{"session":"lead-2","repo":"beta","day":"2026-11-03","source":"startup","at":"2026-11-03T01:10:00Z","pid":14}""",
                "not json",
            });
            File.WriteAllText(Path.Combine(dir, ".coordinator-2026-11-03-alpha"), "lead-1\n");
            File.WriteAllText(Path.Combine(dir, ".coordinator-2026-11-03-beta"), "lead-2");

            var reader = new HandoverRegistryReader(dir);
            var today = reader.ReadToday(Local(2026, 11, 3, 15, 0));

            Assert.Equal(3, today.Count);
            Assert.False(today.ContainsKey("old-1"));
            Assert.Equal(SessionRole.Leader, today["lead-1"].Role);
            Assert.Equal(SessionRole.Member, today["mem-1"].Role);
            Assert.Equal(13, today["mem-1"].Pid); // the resume entry wins
            Assert.Equal(SessionRole.Leader, today["lead-2"].Role);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Working_Day_Starts_At_Four()
    {
        Assert.Equal("2026-11-02", HandoverRegistryReader.WorkingDay(Local(2026, 11, 3, 3, 59)));
        Assert.Equal("2026-11-03", HandoverRegistryReader.WorkingDay(Local(2026, 11, 3, 4, 0)));
    }

    [Fact]
    public void Missing_Directory_Yields_Nothing()
    {
        var reader = new HandoverRegistryReader(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid()));
        Assert.Empty(reader.ReadToday(DateTimeOffset.UtcNow));
    }
}

public class ClaudeProviderMultiSessionTests
{
    private sealed class FakeProcesses : IProcessInspector
    {
        public HashSet<int> Alive { get; } = new();
        public Dictionary<int, int> Parents { get; } = new();
        public int? Foreground { get; set; }

        public List<ClaudeProcess>? Claude { get; set; }

        public bool IsAlive(int pid) => Alive.Contains(pid);
        public IReadOnlyDictionary<int, int> ParentMap() => Parents;
        public int? ForegroundProcessId() => Foreground;
        public IReadOnlyList<ClaudeProcess>? ClaudeProcesses() => Claude;
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private static void Transcript(string root, string id, string cwd, DateTimeOffset now, string? branch = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "P-" + id)).FullName;
        var b = branch is null ? "" : $",\"gitBranch\":\"{branch}\"";
        File.WriteAllLines(Path.Combine(dir, id + ".jsonl"), new[]
        {
            $$$"""{"type":"user","timestamp":"{{{now.AddMinutes(-2):O}}}","cwd":"{{{Json(cwd)}}}"{{{b}}},"message":{"role":"user","content":"hi"}}""",
            Fx.Assistant(now.AddMinutes(-1), "claude-opus-5-5", "m-" + id, "r-" + id, cwd: Json(cwd)),
        });
    }

    private static ClaudeProvider Provider(string root, string handover, FakeProcesses processes, DateTimeOffset now, int endedHide = 0) =>
        new(new TestOptionsMonitor<HudOptions>(new HudOptions { ClaudeProjectsRoot = root, HandoverDir = handover, EndedHideMinutes = endedHide }),
            new ModelCatalog(), new FakeTimeProvider(now), processes,
            NullLogger<ClaudeProvider>.Instance, new ClaudeSettingsReader(Path.Combine(handover, "no-settings.json")));

    [Fact]
    public async Task Unregistered_Worktree_Session_Joins_Its_Main_Repo_As_Member()
    {
        var root = Fx.TempDir();
        var handover = Fx.TempDir();
        var work = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var day = HandoverRegistryReader.WorkingDay(now);

            // <work>/mainrepo/.git/worktrees/ix  ←  <work>/mainrepo-wt-ix/.git (file)
            var main = Directory.CreateDirectory(Path.Combine(work, "mainrepo")).FullName;
            var gitDir = Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "ix")).FullName;
            File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
            var wt = Directory.CreateDirectory(Path.Combine(work, "mainrepo-wt-ix")).FullName;
            File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + gitDir.Replace('\\', '/') + "\n");

            Transcript(root, "lead-1", main, now);
            Transcript(root, "wt-1", wt, now, "wt/ix");
            File.WriteAllLines(Path.Combine(handover, ".sessions.jsonl"), new[]
            {
                $$"""{"session":"lead-1","repo":"mainrepo","day":"{{day}}","at":"{{now.AddHours(-1):O}}","pid":101}""",
            });
            File.WriteAllText(Path.Combine(handover, $".coordinator-{day}-mainrepo"), "lead-1");

            var processes = new FakeProcesses
            {
                Claude = new() { new ClaudeProcess(77, "claude.exe --session-id wt-1 \"go\"", wt) },
            };
            processes.Alive.Add(101);

            var snap = await Provider(root, handover, processes, now).GetAsync();

            var group = Assert.Single(snap.Groups);
            Assert.Equal("mainrepo", group.Name);
            Assert.Equal(new[] { "lead-1", "wt-1" }, group.Sessions.Select(s => s.SessionId));
            Assert.Equal(SessionRole.Leader, group.Sessions[0].Role);
            Assert.Equal(SessionRole.Member, group.Sessions[1].Role);
            Assert.Equal(77, group.Sessions[1].Pid);
            Assert.NotEqual(SessionActivity.Ended, group.Sessions[1].Activity);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(handover, true);
            Directory.Delete(work, true);
        }
    }

    [Fact]
    public async Task Removed_Worktree_Falls_Back_To_The_Wt_Folder_Name_And_Branch()
    {
        var root = Fx.TempDir();
        var handover = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var gone = Path.Combine(handover, "missing", "mcp-local-wt-index-core");
            Transcript(root, "wt-2", gone, now, "wt/index-core");
            Transcript(root, "odd-1", Path.Combine(handover, "missing", "x-wt-a"), now, "main"); // branch does not match

            var snap = await Provider(root, handover, new FakeProcesses(), now).GetAsync(); // no process list: age rule

            var names = snap.Groups.Select(g => g.Name).ToList();
            Assert.Contains("mcp-local", names);
            Assert.Contains("x-wt-a", names);
            var row = snap.Groups.Single(g => g.Name == "mcp-local").Sessions.Single();
            Assert.Equal(SessionRole.Member, row.Role);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(handover, true);
        }
    }

    [Fact]
    public async Task Unregistered_Session_Without_A_Matching_Process_Is_Ended_And_Hidden()
    {
        var root = Fx.TempDir();
        var handover = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            Transcript(root, "dead-1", @"D:\Work\dead", now);
            Transcript(root, "bare-1", @"D:\Work\bare", now);

            var processes = new FakeProcesses
            {
                Claude = new()
                {
                    new ClaudeProcess(10, "claude.exe", @"D:\Work\bare\"),                               // bare claude in bare-1's cwd
                    new ClaudeProcess(11, "claude.exe --session-id 0d48c265-0000-0000-0000-000000000000", @"D:\Work\dead"), // bound elsewhere
                },
            };

            var snap = await Provider(root, handover, processes, now).GetAsync();
            var row = snap.Groups.SelectMany(g => g.Sessions).Single();
            Assert.Equal("bare-1", row.SessionId);
            Assert.Equal(10, row.Pid);

            // EndedHideMinutes keeps an exited one visible for a while, marked Ended.
            var lingering = await Provider(root, handover, processes, now, endedHide: 5).GetAsync();
            var dead = lingering.Groups.SelectMany(g => g.Sessions).Single(s => s.SessionId == "dead-1");
            Assert.Equal(SessionActivity.Ended, dead.Activity);

            // A process that cannot be read means "cannot tell": the transcript-age rule stands.
            processes.Claude.Add(new ClaudeProcess(12, null, null));
            var unknown = await Provider(root, handover, processes, now).GetAsync();
            Assert.Equal(2, unknown.Groups.SelectMany(g => g.Sessions).Count());
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(handover, true);
        }
    }

    [Fact]
    public async Task Groups_Sessions_By_Repo_With_Roles_Focus_And_Liveness()
    {
        var root = Fx.TempDir();
        var handover = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var day = HandoverRegistryReader.WorkingDay(now);

            void Session(string dir, string id, string model, long input, string cwd)
            {
                var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, dir)).FullName, id + ".jsonl");
                File.WriteAllLines(path, new[]
                {
                    Fx.User(now.AddMinutes(-2), cwd),
                    Fx.Assistant(now.AddMinutes(-1), model, "m-" + id, "r-" + id, input: input, output: 10, cwd: cwd),
                });
            }

            Session("D--Work-alpha", "lead-1", "claude-opus-5-5", 100_000, @"D:\\Work\\alpha");
            Session("D--Work-alpha", "mem-1", "claude-sonnet-5-5", 500_000, @"D:\\Work\\alpha");
            Session("D--Work-alpha", "gone-1", "claude-opus-5-5", 1_000, @"D:\\Work\\alpha");
            Session("D--Scratch-tool", "solo-1", "claude-haiku-4-5", 20_000, @"D:\\Scratch\\tool");

            File.WriteAllLines(Path.Combine(handover, ".sessions.jsonl"), new[]
            {
                $$"""{"session":"lead-1","repo":"alpha","day":"{{day}}","at":"{{now.AddHours(-1):O}}","pid":101}""",
                $$"""{"session":"mem-1","repo":"alpha","day":"{{day}}","at":"{{now.AddMinutes(-30):O}}","pid":102}""",
                $$"""{"session":"gone-1","repo":"alpha","day":"{{day}}","at":"{{now.AddMinutes(-50):O}}","pid":103}""",
            });
            File.WriteAllText(Path.Combine(handover, $".coordinator-{day}-alpha"), "lead-1");

            var processes = new FakeProcesses { Foreground = 900 };
            processes.Alive.UnionWith(new[] { 101, 102 }); // 103 has exited: hidden at once, though its transcript is fresh
            processes.Parents[102] = 500; // mem-1's claude.exe → shell 500 → terminal 900
            processes.Parents[500] = 900;
            processes.Parents[101] = 600; // the leader runs in another window

            var options = new TestOptionsMonitor<HudOptions>(new HudOptions
            {
                ClaudeProjectsRoot = root,
                HandoverDir = handover,
            });
            var provider = new ClaudeProvider(options, new ModelCatalog(), new FakeTimeProvider(now), processes,
                NullLogger<ClaudeProvider>.Instance, new ClaudeSettingsReader(Path.Combine(handover, "no-settings.json")));

            var snap = await provider.GetAsync();

            Assert.Equal(2, snap.Groups.Count);
            var alpha = snap.Groups[0];
            Assert.Equal("alpha", alpha.Name);
            Assert.Equal(new[] { "lead-1", "mem-1" }, alpha.Sessions.Select(s => s.SessionId));
            Assert.Equal(SessionRole.Leader, alpha.Sessions[0].Role);
            Assert.Equal(SessionRole.Member, alpha.Sessions[1].Role);
            Assert.Equal(10.0, alpha.Sessions[0].ContextPercent, 3);   // 100k of Opus 5.5's 1M
            Assert.Equal(50.0, alpha.Sessions[1].ContextPercent, 3);   // 500k of Sonnet 5.5's 1M
            Assert.Equal("Opus 5.5", alpha.Sessions[0].ModelLabel);
            Assert.True(alpha.Sessions[1].IsFocused);
            Assert.False(alpha.Sessions[0].IsFocused);

            var solo = snap.Groups[1];
            Assert.Equal("tool", solo.Name);
            Assert.Equal(SessionRole.None, solo.Sessions[0].Role);
            Assert.Equal(10.0, solo.Sessions[0].ContextPercent, 3);    // 20k of Haiku 4.5's 200k

            // The focused session is the primary slice for single-session consumers.
            Assert.Equal("claude-sonnet-5-5", snap.Session.Model);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(handover, true);
        }
    }

    [Fact]
    public void Per_Session_Context_Alerts_Fire_For_Members()
    {
        static SessionSummary S(string id, double pct) => new(id, "alpha", SessionRole.Member, "m", "M", "p", "",
            (long)(pct * 10_000), 1_000_000, 0, 1, null, null, null, false, SessionActivity.Active);

        HudSnapshot Snap(params SessionSummary[] s) => HudSnapshot.Empty with
        {
            HasActiveSession = true,
            Groups = new[] { new SessionGroup("alpha", s) },
        };

        var eval = new AlertEvaluator(new TestOptionsMonitor<AlertOptions>(new AlertOptions { EnableIdle = false }));
        var now = DateTimeOffset.UnixEpoch;

        var first = eval.Evaluate(Snap(S("aaaaaaaa1", 10), S("bbbbbbbb2", 91)), now);
        var alert = Assert.Single(first);
        Assert.Equal(AlertKind.ContextHigh, alert.Kind);
        Assert.Contains("bbbbbbbb", alert.Title);

        Assert.Empty(eval.Evaluate(Snap(S("aaaaaaaa1", 10), S("bbbbbbbb2", 92)), now));
        Assert.Single(eval.Evaluate(Snap(S("aaaaaaaa1", 85), S("bbbbbbbb2", 92)), now));
    }

}

public class LargeTranscriptTests
{
    private sealed class NoProcesses : IProcessInspector
    {
        public bool IsAlive(int pid) => true;
        public IReadOnlyDictionary<int, int> ParentMap() => new Dictionary<int, int>();
        public int? ForegroundProcessId() => null;
    }

    [Fact]
    public async Task Large_Backlog_Shows_Latest_Context_At_Once_And_Catches_Up_Incrementally()
    {
        var root = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "D--Work-big")).FullName, "big-1.jsonl");
            var pad = new string('z', 2000);
            using (var w = new StreamWriter(path))
            {
                // ~20 MB of history, then the latest turn at 250k context.
                for (var i = 0; i < 9000; i++)
                {
                    w.WriteLine($$$"""{"type":"user","timestamp":"{{{now.AddHours(-2).AddSeconds(i):O}}}","cwd":"D:/Work/big","message":{"role":"user","content":[{"type":"tool_result","content":"{{{pad}}}"}]}}""");
                    if (i % 10 == 0)
                    {
                        w.WriteLine(Fx.Assistant(now.AddHours(-2).AddSeconds(i), "claude-opus-5-5", $"m{i}", $"r{i}", input: 1000, cwd: "D:/Work/big"));
                    }
                }
                w.WriteLine(Fx.Assistant(now.AddSeconds(-5), "claude-opus-5-5", "last", "last", input: 250_000, cwd: "D:/Work/big"));
            }

            var options = new TestOptionsMonitor<HudOptions>(new HudOptions { ClaudeProjectsRoot = root, HandoverDir = root });
            var provider = new ClaudeProvider(options, new ModelCatalog(), new FakeTimeProvider(now), new NoProcesses(),
                NullLogger<ClaudeProvider>.Instance, new ClaudeSettingsReader(Path.Combine(root, "none.json")));

            var first = await provider.GetAsync();
            var row = first.Groups.Single().Sessions.Single();
            Assert.Equal(25.0, row.ContextPercent, 3); // from the tail peek, before the backlog is folded

            ClaudeSnapshot last = first;
            for (var i = 0; i < 20; i++)
            {
                last = await provider.GetAsync();
            }

            // Fully folded: every response counted once (900 history turns + the latest one).
            Assert.Equal(901, last.Requests.Requests);
            Assert.Equal(25.0, last.Groups.Single().Sessions.Single().ContextPercent, 3);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class DecisionMarkerTests
{
    private sealed class NoProcesses : IProcessInspector
    {
        public bool IsAlive(int pid) => true;
        public IReadOnlyDictionary<int, int> ParentMap() => new Dictionary<int, int>();
        public int? ForegroundProcessId() => null;
    }

    [Fact]
    public async Task Hook_Marker_Shows_An_Open_Question_Until_The_Transcript_Has_The_Answer()
    {
        var root = Fx.TempDir();
        var markers = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "D--Work-alpha")).FullName, "s-1.jsonl");
            File.WriteAllLines(path, new[] { Fx.User(now.AddSeconds(-20)), Fx.Assistant(now.AddSeconds(-10), "claude-opus-5-5", "m1", "r1") });

            // Claude Code has not written the question yet; only the hook knows it is open.
            File.WriteAllText(Path.Combine(markers, "s-1.json"),
                $$"""{"session_id":"s-1","tool_use_id":"toolu_q1","tool":"AskUserQuestion","header":"Pick","question":"Which one?","at":"{{now.AddSeconds(-3):O}}"}""");

            var options = new TestOptionsMonitor<HudOptions>(new HudOptions { ClaudeProjectsRoot = root, HandoverDir = root, DecisionMarkerDir = markers });
            var provider = new ClaudeProvider(options, new ModelCatalog(), new FakeTimeProvider(now), new NoProcesses(),
                NullLogger<ClaudeProvider>.Instance, new ClaudeSettingsReader(Path.Combine(root, "none.json")));

            var open = (await provider.GetAsync()).Groups.Single().Sessions.Single();
            Assert.NotNull(open.Decision);
            Assert.Equal("Pick", open.Decision!.Header);
            Assert.Equal("Which one?", open.Decision.Question);

            // Answered: the transcript now holds the result, even if the marker lingers.
            File.AppendAllLines(path, new[]
            {
                $$$"""{"type":"user","timestamp":"{{{now:O}}}","cwd":"D:/Work/alpha","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_q1","content":"A"}]}}""",
            });
            var answered = (await provider.GetAsync()).Groups.Single().Sessions.Single();
            Assert.Null(answered.Decision);

            // A marker for another prompt with no transcript answer shows again; removing it clears it.
            File.WriteAllText(Path.Combine(markers, "s-1.json"), """{"session_id":"s-1","tool_use_id":"toolu_q2","tool":"ExitPlanMode"}""");
            Assert.Equal("plan", (await provider.GetAsync()).Groups.Single().Sessions.Single().Decision?.Kind);
            File.Delete(Path.Combine(markers, "s-1.json"));
            Assert.Null((await provider.GetAsync()).Groups.Single().Sessions.Single().Decision);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(markers, true);
        }
    }
}
