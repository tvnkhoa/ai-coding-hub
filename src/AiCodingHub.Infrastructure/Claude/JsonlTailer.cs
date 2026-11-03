using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Append-only incremental reader for a single JSONL file. Remembers a byte offset and only
/// returns complete lines appended since the last read; a trailing partial line is re-read on
/// the next call once its newline arrives. Opens with <see cref="FileShare.ReadWrite"/> so it
/// never blocks Claude from writing, and starts over when the file shrinks (truncation).
/// <para>
/// <c>maxBytes</c> bounds the work of one call so a very large backlog is consumed over several
/// polls instead of stalling one; <see cref="IsAtEnd"/> reports whether the backlog is drained.
/// </para>
/// </summary>
public sealed class JsonlTailer
{
    private string? _path;
    private long _offset;

    public JsonlTailer(string? path = null) => _path = path;

    /// <summary>Byte offset of the next unread line.</summary>
    public long Offset => _offset;

    /// <summary>File length seen by the last read.</summary>
    public long Length { get; private set; }

    /// <summary>True once every complete line up to the last-seen length has been returned.</summary>
    public bool IsAtEnd { get; private set; } = true;

    /// <summary>Point the tailer at a new file (resets the offset).</summary>
    public void Reset(string? path)
    {
        _path = path;
        _offset = 0;
        Length = 0;
        IsAtEnd = true;
    }

    public IReadOnlyList<string> ReadNewLines(long maxBytes = long.MaxValue)
    {
        var lines = new List<string>();
        if (_path is null || !File.Exists(_path))
        {
            return lines;
        }

        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        Length = length;

        if (length < _offset)
        {
            _offset = 0; // file shrank — truncated or replaced; start over
        }

        var available = length - _offset;
        if (available <= 0)
        {
            IsAtEnd = true;
            return lines;
        }

        var budget = (int)Math.Min(Math.Min(available, Math.Max(1, maxBytes)), int.MaxValue - 64);
        fs.Seek(_offset, SeekOrigin.Begin);
        var buffer = ReadExactly(fs, budget);

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
        // A single line longer than the budget: keep reading until its newline (or EOF).
        while (lastNewline < 0 && _offset + buffer.Length < length)
        {
            var more = ReadExactly(fs, (int)Math.Min(Math.Max(buffer.Length, 1 << 20), length - _offset - buffer.Length));
            var start = buffer.Length;
            Array.Resize(ref buffer, buffer.Length + more.Length);
            Buffer.BlockCopy(more, 0, buffer, start, more.Length);
            var idx = Array.IndexOf(more, (byte)'\n');
            if (idx >= 0)
            {
                lastNewline = start + idx;
            }
        }

        if (lastNewline < 0)
        {
            IsAtEnd = true; // only a partial line so far — wait for its newline
            return lines;
        }

        // Drained when this read reached EOF: whatever follows the last newline is a partial line.
        var coveredToEof = _offset + buffer.Length >= length;
        var text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        _offset += lastNewline + 1;
        IsAtEnd = coveredToEof;

        var pos = 0;
        int nl;
        while ((nl = text.IndexOf('\n', pos)) >= 0)
        {
            var line = text.AsSpan(pos, nl - pos).TrimEnd('\r');
            if (line.Length > 0)
            {
                lines.Add(line.ToString());
            }
            pos = nl + 1;
        }

        return lines;
    }

    private static byte[] ReadExactly(FileStream fs, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = fs.Read(buffer, read, count - read);
            if (n == 0)
            {
                break;
            }
            read += n;
        }

        if (read < count)
        {
            Array.Resize(ref buffer, read);
        }
        return buffer;
    }
}
