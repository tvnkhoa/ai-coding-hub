using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Pricing;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Live state of one session transcript: an incremental tailer plus a running
/// <see cref="SessionAccumulator"/>, so each poll reads only appended bytes and switching
/// between sessions costs nothing.
/// <para>
/// A large backlog (a long session seen for the first time) is consumed in bounded chunks over
/// several polls. Until it is drained, the context %, model and workspace come from a quick
/// <b>tail peek</b> — the last ~1 MB parsed once at creation — so the row is correct immediately.
/// </para>
/// </summary>
public sealed class SessionTracker
{
    private const int PeekBytes = 1 << 20;

    private readonly TranscriptParser _parser;
    private readonly IPricingCatalog _pricing;
    private readonly JsonlTailer _tailer;
    private readonly SessionAccumulator _acc;
    private SessionAccumulator? _peek;
    private bool _pumped;

    public SessionTracker(SessionFile file, TranscriptParser parser, IPricingCatalog pricing, long peekThresholdBytes)
    {
        File = file;
        _parser = parser;
        _pricing = pricing;
        _tailer = new JsonlTailer(file.Path);
        _acc = new SessionAccumulator(pricing);

        try
        {
            var length = new FileInfo(file.Path).Length;
            if (length > peekThresholdBytes)
            {
                _peek = Peek(file.Path, length);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public SessionFile File { get; set; }

    /// <summary>True once the whole file has been folded into the accumulator.</summary>
    public bool CaughtUp => _pumped && _tailer.IsAtEnd;

    /// <summary>Reads up to <paramref name="maxBytes"/> of new lines into the accumulator.</summary>
    public void Pump(long maxBytes)
    {
        foreach (var line in _tailer.ReadNewLines(maxBytes))
        {
            if (_parser.ParseLine(line) is { } ev)
            {
                _acc.Add(ev);
            }
        }

        _pumped = true;
        if (_tailer.IsAtEnd)
        {
            _peek = null; // the full fold now knows the latest turn
        }
    }

    public ClaudeSnapshot Snapshot(TimeSpan idleThreshold, DateTimeOffset now)
    {
        var full = _acc.ToSnapshot(idleThreshold, now);
        if (_peek is null || CaughtUp)
        {
            return full;
        }

        // Still catching up: latest-turn facts from the tail, running totals from the fold so far.
        var tail = _peek.ToSnapshot(idleThreshold, now);
        if (!tail.HasActiveSession)
        {
            return full;
        }

        return tail with
        {
            Name = tail.Name ?? full.Name,
            Session = tail.Session with { StartedAt = full.Session.StartedAt ?? tail.Session.StartedAt },
            Cost = full.HasActiveSession ? full.Cost : tail.Cost,
            Requests = full.HasActiveSession ? full.Requests : tail.Requests,
        };
    }

    private SessionAccumulator? Peek(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, length - PeekBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[length - start];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var lines = text.Split('\n');
        var acc = new SessionAccumulator(_pricing);
        // The first fragment may be a partial line when we started mid-file.
        for (var i = start > 0 ? 1 : 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0 && _parser.ParseLine(line) is { } ev)
            {
                acc.Add(ev);
            }
        }

        return acc.EventCount > 0 ? acc : null;
    }
}
