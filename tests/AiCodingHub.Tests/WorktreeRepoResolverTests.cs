using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AiCodingHub.Infrastructure.Claude;

namespace AiCodingHub.Tests;

/// <summary>Path aliases (8.3, junction, separators, case) name one directory; failures fall back.</summary>
public class WorktreeRepoResolverTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint length);

    private static string? ShortPath(string path)
    {
        var buffer = new StringBuilder(1024);
        var n = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        return n == 0 || n >= buffer.Capacity ? null : buffer.ToString(0, (int)n);
    }

    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void Short_83_Path_Equals_Long_Path()
    {
        var root = Fx.TempDir();
        try
        {
            var longDir = Directory.CreateDirectory(Path.Combine(root, "a rather long folder name")).FullName;
            var shortDir = ShortPath(longDir);
            Assert.NotNull(shortDir);
            if (string.Equals(shortDir, longDir, StringComparison.OrdinalIgnoreCase))
            {
                return; // 8.3 name generation is off on this volume: nothing to resolve
            }

            Assert.Contains("~", shortDir!);
            Assert.Equal(WorktreeRepoResolver.CanonicalPath(longDir), WorktreeRepoResolver.CanonicalPath(shortDir!));
            Assert.DoesNotContain("~", WorktreeRepoResolver.CanonicalPath(shortDir!));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Junction_Resolves_To_Its_Target_And_Names_The_Target_Repo()
    {
        var root = Fx.TempDir();
        var link = Path.Combine(root, "alias");
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(root, "realrepo")).FullName;
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var sub = Directory.CreateDirectory(Path.Combine(repo, "src")).FullName;
            Junction(link, repo);

            Assert.Equal(WorktreeRepoResolver.CanonicalPath(sub), WorktreeRepoResolver.CanonicalPath(Path.Combine(link, "src")));
            Assert.Equal(WorktreeRepoResolver.CanonicalPath(repo), WorktreeRepoResolver.CanonicalPath(link + "\\"));
            Assert.Equal(("realrepo", false), WorktreeRepoResolver.Resolve(link, "main"));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link); // removes the junction only, never its target
            }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Trailing_Separator_Slashes_And_Case_Name_One_Directory()
    {
        var root = Fx.TempDir();
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "MixedCase")).FullName;
            var canonical = WorktreeRepoResolver.CanonicalPath(dir);

            Assert.Equal(canonical, WorktreeRepoResolver.CanonicalPath(dir + "\\"));
            Assert.Equal(canonical, WorktreeRepoResolver.CanonicalPath(dir.Replace('\\', '/') + "/"));
            Assert.Equal(canonical, WorktreeRepoResolver.CanonicalPath(@"\\?\" + dir));
            Assert.Equal(canonical, WorktreeRepoResolver.CanonicalPath(dir.ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
            Assert.EndsWith("MixedCase", WorktreeRepoResolver.CanonicalPath(dir.ToLowerInvariant()), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Missing_Path_Falls_Back_To_String_Normalisation_And_Resolves_Once_It_Exists()
    {
        var root = Fx.TempDir();
        try
        {
            var missing = Path.Combine(root, "gone", "x") + "\\";
            Assert.Equal(WorktreeRepoResolver.NormalizePath(missing), WorktreeRepoResolver.CanonicalPath(missing));

            var made = Directory.CreateDirectory(Path.Combine(root, "gone", "x")).FullName;
            Assert.Equal(WorktreeRepoResolver.CanonicalPath(made), WorktreeRepoResolver.CanonicalPath(missing));

            Assert.Equal("", WorktreeRepoResolver.CanonicalPath(""));
            Assert.Equal(@"Q:\no\such|name", WorktreeRepoResolver.CanonicalPath(@"Q:\no\such|name"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Unc_Path_Is_Returned_Without_Probing()
    {
        var started = Stopwatch.StartNew();
        Assert.Equal(@"\\no-such-host-hud\share\repo", WorktreeRepoResolver.CanonicalPath(@"\\no-such-host-hud\share\repo\"));
        Assert.Equal(@"\\no-such-host-hud\share\a", WorktreeRepoResolver.CanonicalPath(@"\\?\UNC\no-such-host-hud\share\a"));
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(500), $"took {started.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Cached_Lookups_Stay_Cheap()
    {
        var root = Fx.TempDir();
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "hot")).FullName;
            WorktreeRepoResolver.CanonicalPath(dir);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 10_000; i++)
            {
                WorktreeRepoResolver.CanonicalPath(dir);
            }
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(200), $"10k cached lookups took {watch.ElapsedMilliseconds} ms");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
