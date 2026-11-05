using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AiCodingHub.Core.Options;
using AiCodingHub.Core.Pricing;
using AiCodingHub.Infrastructure.Claude;
using AiCodingHub.Infrastructure.System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiCodingHub.Tests;

/// <summary>
/// EnterWorktree switches the transcript's line cwd and chdirs claude.exe into the worktree; an
/// unregistered session must still match its process (first or latest cwd).
/// </summary>
public class EnterWorktreeCwdTests
{
    private const string Start = @"D:\Work\mainrepo";
    private const string Worktree = @"D:\Work\mainrepo\.claude\worktrees\probe";

    private sealed class FixedProcesses(ProcessSnapshot snapshot) : IProcessInspector
    {
        public bool IsAlive(int pid) => snapshot.Live?.Contains(pid) ?? false;
        public IReadOnlyDictionary<int, int> ParentMap() => snapshot.Parents;
        public int? ForegroundProcessId() => null;
        public IReadOnlyList<ClaudeProcess>? ClaudeProcesses() => snapshot.Claude;
        public ProcessSnapshot Snapshot() => snapshot;
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private static string[] SwitchedTranscript(DateTimeOffset now) => new[]
    {
        Fx.User(now.AddMinutes(-5), Json(Start)),
        Fx.Assistant(now.AddMinutes(-4), "claude-opus-5-5", "m-1", "r-1", cwd: Json(Start)),
        Fx.User(now.AddMinutes(-2), Json(Worktree)),
        Fx.Assistant(now.AddMinutes(-1), "claude-opus-5-5", "m-2", "r-2", cwd: Json(Worktree)),
    };

    private static async Task<int?> PidShown(string processCwd)
    {
        var root = Fx.TempDir();
        var handover = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var dir = Directory.CreateDirectory(Path.Combine(root, "D--Work-mainrepo--claude-worktrees-probe")).FullName;
            File.WriteAllLines(Path.Combine(dir, "ewt-1.jsonl"), SwitchedTranscript(now));

            var processes = new ProcessSnapshot(new Dictionary<int, int>(),
                new[] { new ClaudeProcess(77, "claude.exe", processCwd) }, new HashSet<int> { 77 });
            var provider = new ClaudeProvider(
                new TestOptionsMonitor<HudOptions>(new HudOptions { ClaudeProjectsRoot = root, HandoverDir = handover }),
                new ModelCatalog(), new FakeTimeProvider(now), new FixedProcesses(processes),
                NullLogger<ClaudeProvider>.Instance, new ClaudeSettingsReader(Path.Combine(handover, "no-settings.json")));

            var snap = await provider.GetAsync();
            return snap.Groups.SelectMany(g => g.Sessions).SingleOrDefault(s => s.SessionId == "ewt-1")?.Pid;
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(handover, true);
        }
    }

    [Fact]
    public void Accumulator_Keeps_The_Launch_Folder_And_The_Latest_Cwd()
    {
        var now = DateTimeOffset.UtcNow;
        var parser = new TranscriptParser();
        var acc = new SessionAccumulator(new ModelCatalog());
        foreach (var line in SwitchedTranscript(now))
        {
            if (parser.ParseLine(line) is { } ev)
            {
                acc.Add(ev);
            }
        }

        var session = acc.ToSnapshot(TimeSpan.FromMinutes(5), now).Session;
        Assert.Equal(Start, session.Workspace);
        Assert.Equal(Worktree, session.CurrentCwd);
    }

    [Fact]
    public async Task Bare_Claude_Chdired_Into_The_Worktree_Matches_Its_Session()
    {
        Assert.Equal(77, await PidShown(Worktree + "\\"));
    }

    [Fact]
    public async Task Bare_Claude_With_A_Cached_Start_Cwd_Still_Matches()
    {
        Assert.Equal(77, await PidShown(Start));
    }

    [Fact]
    public async Task Bare_Claude_Elsewhere_Leaves_The_Session_Ended_And_Hidden()
    {
        Assert.Null(await PidShown(@"D:\Work\other"));
    }
}
