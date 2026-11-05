using System;
using System.IO;
using AiCodingHub.Infrastructure.Claude;

namespace AiCodingHub.Tests;

/// <summary>Registry reading incl. the hook's `.sessions.jsonl.&lt;pid&gt;.trimming` side file; temp dirs only.</summary>
public class HandoverRegistryReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"aicodinghub-registry-{Guid.NewGuid():N}");

    public HandoverRegistryReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static string Row(string session, int pid, int minute) =>
        $"{{\"session\":\"{session}\",\"repo\":\"r\",\"day\":\"{HandoverRegistryReader.WorkingDay(Now)}\"," +
        $"\"at\":\"2026-11-05T10:{minute:00}:00Z\",\"pid\":{pid}}}";

    private void Write(string name, params string[] rows) =>
        File.WriteAllText(Path.Combine(_dir, name), string.Join("\n", rows) + "\n");

    [Fact]
    public void Reads_Main_File_Only()
    {
        Write(".sessions.jsonl", Row("a", 1, 1), Row("b", 2, 2));
        var result = new HandoverRegistryReader(_dir).ReadToday(Now);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result["b"].Pid);
    }

    [Fact]
    public void Rows_Split_Between_Main_And_Trimming_Are_All_Seen()
    {
        Write(".sessions.jsonl", Row("b", 2, 2));
        Write(".sessions.jsonl.4242.trimming", Row("a", 1, 1));
        var result = new HandoverRegistryReader(_dir).ReadToday(Now);
        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("a"));
        Assert.True(result.ContainsKey("b"));
    }

    [Fact]
    public void Newest_Row_Wins_Across_Files()
    {
        Write(".sessions.jsonl", Row("a", 1, 1));
        Write(".sessions.jsonl.4242.trimming", Row("a", 9, 5));
        var result = new HandoverRegistryReader(_dir).ReadToday(Now);
        Assert.Single(result);
        Assert.Equal(9, result["a"].Pid);
    }

    [Fact]
    public void Trimming_Only_When_Main_Is_Absent()
    {
        Write(".sessions.jsonl.4242.trimming", Row("a", 1, 1));
        var result = new HandoverRegistryReader(_dir).ReadToday(Now);
        Assert.Single(result);
    }

    [Fact]
    public void Cache_Does_Not_Hide_A_Trimming_File_Appearing_Later()
    {
        Write(".sessions.jsonl", Row("b", 2, 2));
        var reader = new HandoverRegistryReader(_dir);
        Assert.Single(reader.ReadToday(Now));
        Write(".sessions.jsonl.4242.trimming", Row("a", 1, 1));
        Assert.Equal(2, reader.ReadToday(Now).Count);
        File.Delete(Path.Combine(_dir, ".sessions.jsonl.4242.trimming"));
        Assert.Single(reader.ReadToday(Now));
    }

    [Fact]
    public void No_Files_Gives_Empty_Without_Throwing()
    {
        Assert.Empty(new HandoverRegistryReader(_dir).ReadToday(Now));
    }
}
