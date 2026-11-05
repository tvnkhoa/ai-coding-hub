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

/// <summary>MaxSessions never cuts a session whose process runs; it caps only the rest.</summary>
public class SessionCapTests
{
    private sealed class FixedProcesses(ProcessSnapshot snapshot) : IProcessInspector
    {
        public bool IsAlive(int pid) => snapshot.Live?.Contains(pid) ?? false;
        public IReadOnlyDictionary<int, int> ParentMap() => snapshot.Parents;
        public int? ForegroundProcessId() => null;
        public IReadOnlyList<ClaudeProcess>? ClaudeProcesses() => snapshot.Claude;
        public ProcessSnapshot Snapshot() => snapshot;
    }

    private static string Json(string path) => path.Replace("\\", "\\\\");

    private static void Transcript(string root, string projectDir, string id, string cwd, DateTimeOffset at)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, projectDir)).FullName;
        var file = Path.Combine(dir, id + ".jsonl");
        File.WriteAllLines(file, new[]
        {
            Fx.User(at.AddMinutes(-1), Json(cwd)),
            Fx.Assistant(at, "claude-opus-5-5", "m-" + id, "r-" + id, cwd: Json(cwd)),
        });
        File.SetLastWriteTimeUtc(file, at.UtcDateTime);
    }

    private static async Task<List<string>> Shown(string root, string handover, ProcessSnapshot processes,
        DateTimeOffset now, int cap)
    {
        var provider = new ClaudeProvider(
            new TestOptionsMonitor<HudOptions>(new HudOptions
            {
                ClaudeProjectsRoot = root, HandoverDir = handover, MaxSessions = cap, ActiveWindowMinutes = 60,
            }),
            new ModelCatalog(), new FakeTimeProvider(now), new FixedProcesses(processes),
            NullLogger<ClaudeProvider>.Instance, new ClaudeSettingsReader(Path.Combine(handover, "no-settings.json")));
        var snap = await provider.GetAsync();
        return snap.Groups.SelectMany(g => g.Sessions).Select(s => s.SessionId).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task Fifteen_Live_Registered_Sessions_Over_A_Cap_Of_12_Are_All_Shown()
    {
        var root = Fx.TempDir();
        var handover = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var day = HandoverRegistryReader.WorkingDay(now);
            var lines = new List<string>();
            var live = new HashSet<int>();
            for (var i = 0; i < 15; i++)
            {
                var id = $"live-{i:D2}";
                Transcript(root, "P-" + id, id, $@"D:\Work\r{i}", now.AddMinutes(-i));
                lines.Add($$"""{"session":"{{id}}","repo":"r{{i}}","day":"{{day}}","at":"{{now.AddHours(-1):O}}","pid":{{1000 + i}}}""");
                live.Add(1000 + i);
            }
            File.WriteAllLines(Path.Combine(handover, ".sessions.jsonl"), lines);

            var shown = await Shown(root, handover,
                new ProcessSnapshot(new Dictionary<int, int>(), Array.Empty<ClaudeProcess>(), live), now, cap: 12);

            Assert.Equal(15, shown.Count);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(handover, true);
        }
    }

    [Fact]
    public async Task Live_Sessions_Stay_And_Stale_Ones_Over_The_Cap_Are_Cut_Newest_First()
    {
        var root = Fx.TempDir();
        var handover = Fx.TempDir();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var day = HandoverRegistryReader.WorkingDay(now);

            // Live, all older than every stale one: registered pid alive, claimed by command line,
            // and a bare claude.exe in the cwd its project folder encodes.
            Transcript(root, "P-reg", "reg-live", @"D:\Work\a", now.AddMinutes(-50));
            var registry = new List<string>
            {
                $$"""{"session":"reg-live","repo":"a","day":"{{day}}","at":"{{now.AddHours(-1):O}}","pid":101}""",
            };
            const string claimedId = "0a0a0a0a-1111-2222-3333-444444444444";
            Transcript(root, "P-claimed", claimedId, @"D:\Work\c", now.AddMinutes(-49));
            Transcript(root, "D--Work-bare", "bare-live", @"D:\Work\bare", now.AddMinutes(-48));

            // Stale: registered without a pid, within the active window, so no known process.
            for (var i = 0; i < 5; i++)
            {
                Transcript(root, "P-stale", $"stale-{i}", @"D:\Work\s", now.AddMinutes(-10 - i));
                registry.Add($$"""{"session":"stale-{{i}}","repo":"s","day":"{{day}}","at":"{{now.AddHours(-1):O}}"}""");
            }
            File.WriteAllLines(Path.Combine(handover, ".sessions.jsonl"), registry);

            var processes = new ProcessSnapshot(
                new Dictionary<int, int>(),
                new[]
                {
                    new ClaudeProcess(201, $"claude.exe --resume {claimedId}", @"D:\Work\c"),
                    new ClaudeProcess(202, "claude.exe", @"D:\Work\bare\"),
                },
                new HashSet<int> { 101, 201, 202 });

            var shown = await Shown(root, handover, processes, now, cap: 4);

            Assert.Equal(new[] { claimedId, "bare-live", "reg-live", "stale-0" }.OrderBy(s => s, StringComparer.Ordinal), shown);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(handover, true);
        }
    }
}
