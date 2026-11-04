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

/// <summary>The 2 s session poll: one process pass, cached PEB reads, no slow file-system probes.</summary>
public class ProcessPollPerfTests
{
    /// <summary>Counts calls; only <see cref="Snapshot"/> may be used by the poll.</summary>
    private sealed class CountingProcesses : IProcessInspector
    {
        public int Snapshots, ParentMaps, ClaudeLists, AliveChecks;
        public ProcessSnapshot Next { get; set; } = new(new Dictionary<int, int>(), null, new HashSet<int>());

        public bool IsAlive(int pid) { AliveChecks++; return true; }
        public IReadOnlyDictionary<int, int> ParentMap() { ParentMaps++; return Next.Parents; }
        public int? ForegroundProcessId() => null;
        public IReadOnlyList<ClaudeProcess>? ClaudeProcesses() { ClaudeLists++; return Next.Claude; }
        public ProcessSnapshot Snapshot() { Snapshots++; return Next; }
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private static void Transcript(string root, string id, string cwd, DateTimeOffset now)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "P-" + id)).FullName;
        File.WriteAllLines(Path.Combine(dir, id + ".jsonl"), new[]
        {
            Fx.User(now.AddMinutes(-2), Json(cwd)),
            Fx.Assistant(now.AddMinutes(-1), "claude-opus-5-5", "m-" + id, "r-" + id, cwd: Json(cwd)),
        });
    }

    private static ClaudeProvider Provider(string root, string handover, IProcessInspector processes, FakeTimeProvider time) =>
        new(new TestOptionsMonitor<HudOptions>(new HudOptions { ClaudeProjectsRoot = root, HandoverDir = handover }),
            new ModelCatalog(), time, processes,
            NullLogger<ClaudeProvider>.Instance, new ClaudeSettingsReader(Path.Combine(handover, "no-settings.json")));

    [Fact]
    public async Task One_Process_Snapshot_Per_Poll_Serves_Liveness_Parents_And_Claude_List()
    {
        var root = Fx.TempDir();
        var handover = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var day = HandoverRegistryReader.WorkingDay(now);
            Transcript(root, "reg-1", @"D:\Work\a", now);
            Transcript(root, "bare-1", @"D:\Work\b", now);
            File.WriteAllLines(Path.Combine(handover, ".sessions.jsonl"), new[]
            {
                $$"""{"session":"reg-1","repo":"a","day":"{{day}}","at":"{{now.AddHours(-1):O}}","pid":101}""",
            });

            var processes = new CountingProcesses
            {
                Next = new ProcessSnapshot(
                    new Dictionary<int, int> { [101] = 1, [7] = 1 },
                    new[] { new ClaudeProcess(7, "claude.exe", @"D:\Work\b") },
                    new HashSet<int> { 101, 7 }),
            };
            var time = new FakeTimeProvider(now);
            var provider = Provider(root, handover, processes, time);

            var snap = await provider.GetAsync();
            time.Advance(TimeSpan.FromSeconds(2));
            await provider.GetAsync();

            Assert.Equal(2, processes.Snapshots);
            Assert.Equal(0, processes.ParentMaps);
            Assert.Equal(0, processes.ClaudeLists);
            Assert.Equal(0, processes.AliveChecks); // liveness comes from the snapshot
            var rows = snap.Groups.SelectMany(g => g.Sessions).OrderBy(s => s.SessionId).ToList();
            Assert.Equal(new[] { "bare-1", "reg-1" }, rows.Select(s => s.SessionId));
            Assert.Equal(7, rows[0].Pid);

            // Registered pid gone from the snapshot: ended and hidden.
            processes.Next = processes.Next with { Live = new HashSet<int> { 7 } };
            time.Advance(TimeSpan.FromSeconds(2));
            var after = await provider.GetAsync();
            Assert.DoesNotContain(after.Groups.SelectMany(g => g.Sessions), s => s.SessionId == "reg-1");
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(handover, true);
        }
    }

    [Fact]
    public void Peb_Cache_Hits_On_Same_Pid_And_Creation_Time_And_Rereads_A_Reused_Pid()
    {
        var cache = new ProcessParameterCache();
        var reads = 0;
        long created = 100;
        (string?, string?) Read(int pid) { reads++; return ($"claude.exe #{reads}", @"D:\x"); }

        var first = cache.Collect(new[] { 5 }, _ => created, Read);
        var second = cache.Collect(new[] { 5 }, _ => created, Read);
        Assert.Equal(1, reads);
        Assert.Equal(first, second);

        created = 200; // pid 5 reused by a new process
        var third = cache.Collect(new[] { 5 }, _ => created, Read);
        Assert.Equal(2, reads);
        Assert.Equal("claude.exe #2", Assert.Single(third!).CommandLine);
    }

    [Fact]
    public void Peb_Cache_Evicts_Gone_Pids_And_Never_Caches_Unknown_Or_Unreadable()
    {
        var cache = new ProcessParameterCache();
        var reads = 0;
        (string?, string?) Read(int pid) { reads++; return ("claude.exe", @"D:\x"); }

        cache.Collect(new[] { 5, 6 }, _ => 1, Read);
        Assert.Equal(2, cache.Count);
        cache.Collect(new[] { 6 }, _ => 1, Read);
        Assert.Equal(1, cache.Count);
        Assert.Equal(2, reads);
        cache.Collect(new[] { 5, 6 }, _ => 1, Read); // 5 came back: read again
        Assert.Equal(3, reads);

        // No creation time, or nothing readable: read every time.
        cache.Collect(new[] { 9 }, _ => null, Read);
        cache.Collect(new[] { 9 }, _ => null, Read);
        Assert.Equal(5, reads);
        var unreadable = 0;
        cache.Collect(new[] { 9 }, _ => 1, _ => { unreadable++; return (null, null); });
        cache.Collect(new[] { 9 }, _ => 1, _ => { unreadable++; return (null, null); });
        Assert.Equal(2, unreadable);

        Assert.Null(cache.Collect(Array.Empty<int>(), _ => 1, Read));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Unc_Cwd_Skips_The_File_System_Probe_And_Uses_The_Folder_Name()
    {
        Assert.True(WorktreeRepoResolver.IsNetworkPath(@"\\no-such-host-hud\share\repo"));
        Assert.False(WorktreeRepoResolver.IsNetworkPath(Path.GetTempPath()));

        // Unreachable host: answered from the -wt- folder name and branch, without touching the share.
        var started = DateTime.UtcNow;
        Assert.Equal(("mainrepo", true), WorktreeRepoResolver.Resolve(@"\\no-such-host-hud\share\mainrepo-wt-ix", "wt/ix"));
        Assert.Null(WorktreeRepoResolver.Resolve(@"\\?\UNC\no-such-host-hud\share\plain", "main"));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(@"\\?\D:\Work\a\", @"D:\Work\a")]
    [InlineData(@"D:/Work/a/", @"D:\Work\a")]
    [InlineData(@"D:\Work\.\a", @"D:\Work\a")]
    [InlineData(@"\\?\UNC\srv\share\a", @"\\srv\share\a")]
    [InlineData(@"D:\", @"D:\")]
    public void Paths_Are_Normalised_Before_Comparing(string input, string expected)
    {
        Assert.Equal(expected, WorktreeRepoResolver.NormalizePath(input));
    }

    [Fact]
    public async Task Bare_Claude_With_A_Long_Path_Prefixed_Cwd_Matches_Its_Session()
    {
        var root = Fx.TempDir();
        var handover = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            Transcript(root, "bare-2", @"D:\Work\q", now);
            var processes = new CountingProcesses
            {
                Next = new ProcessSnapshot(new Dictionary<int, int>(),
                    new[] { new ClaudeProcess(31, "claude.exe", @"\\?\D:\Work\q\") }, new HashSet<int> { 31 }),
            };

            var snap = await Provider(root, handover, processes, new FakeTimeProvider(now)).GetAsync();

            Assert.Equal(31, snap.Groups.SelectMany(g => g.Sessions).Single().Pid);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(handover, true);
        }
    }
}
