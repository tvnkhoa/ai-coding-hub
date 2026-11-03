using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiCodingHub.Core.Models;
using AiCodingHub.Core.Pricing;

namespace AiCodingHub.Infrastructure.Claude;

/// <summary>
/// Spend across every transcript under the projects root (subagent transcripts included),
/// bucketed by day / week (Monday start) / month in local time, with the day boundary moved to
/// <c>dayStartHour</c>. Keeps per-file state so each scan reads only the bytes appended since the
/// previous one, counts each API response once (Claude Code repeats usage on every content-block
/// line), and skips files last written before the earliest bucket start.
/// Not thread-safe — owned by one background loop.
/// </summary>
public sealed class CostLedger
{
    private sealed class FileState
    {
        public required JsonlTailer Tailer { get; init; }
        public DateTime LastWriteUtc { get; set; }
        public Dictionary<DateOnly, decimal> PerDay { get; } = new();
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    }

    private readonly TranscriptParser _parser = new();
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of files currently tracked (for diagnostics/tests).</summary>
    public int TrackedFiles => _files.Count;

    /// <summary>Total bytes consumed by the last <see cref="Scan"/> (for diagnostics/tests).</summary>
    public long LastScanBytes { get; private set; }

    public CostTotals Scan(string root, IPricingCatalog pricing, DateTimeOffset now, int dayStartHour = 0)
    {
        LastScanBytes = 0;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return CostTotals.Empty;
        }

        var shift = TimeSpan.FromHours(Math.Clamp(dayStartHour, 0, 23));
        var today = WorkingDate(now, shift);
        var weekStart = today.AddDays(-((7 + (int)today.DayOfWeek - (int)DayOfWeek.Monday) % 7));
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var earliest = weekStart < monthStart ? weekStart : monthStart;
        // A file last written before the earliest bucket cannot contribute (lines are appended).
        var cutoffUtc = earliest.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).Add(shift).ToUniversalTime();

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateFilesSafe(root))
        {
            DateTime lastWrite;
            try
            {
                lastWrite = File.GetLastWriteTimeUtc(file);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (lastWrite < cutoffUtc)
            {
                continue;
            }

            present.Add(file);
            if (!_files.TryGetValue(file, out var state))
            {
                state = new FileState { Tailer = new JsonlTailer(file) };
                _files[file] = state;
            }
            else if (state.LastWriteUtc == lastWrite && state.Tailer.IsAtEnd)
            {
                continue; // unchanged since last scan
            }

            state.LastWriteUtc = lastWrite;
            ReadAppended(state, pricing, shift);
        }

        // Forget files that disappeared or aged out of every bucket.
        foreach (var stale in _files.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _files.Remove(stale);
        }

        decimal todayUsd = 0m, weekUsd = 0m, monthUsd = 0m;
        foreach (var state in _files.Values)
        {
            foreach (var (day, cost) in state.PerDay)
            {
                if (day >= monthStart) monthUsd += cost;
                if (day >= weekStart) weekUsd += cost;
                if (day == today) todayUsd += cost;
            }
        }

        return new CostTotals(todayUsd, weekUsd, monthUsd);
    }

    private void ReadAppended(FileState state, IPricingCatalog pricing, TimeSpan shift)
    {
        try
        {
            var before = state.Tailer.Offset;
            foreach (var line in state.Tailer.ReadNewLines())
            {
                // Cheap pre-filter: only assistant lines carry usage.
                if (!line.Contains("\"usage\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var ev = _parser.ParseLine(line);
                if (ev is null || ev.Role != EventRole.Assistant || ev.Usage is null || ev.Timestamp is null)
                {
                    continue;
                }

                if (ev.ResponseKey is { } key && !state.Seen.Add(key))
                {
                    continue;
                }

                var cost = TokenCostCalculator.Cost(ev.Usage, pricing.Resolve(ev.Model));
                if (cost <= 0m)
                {
                    continue;
                }

                var day = WorkingDate(ev.Timestamp.Value, shift);
                state.PerDay[day] = state.PerDay.GetValueOrDefault(day) + cost;
            }

            LastScanBytes += state.Tailer.Offset - before;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Local calendar date of <paramref name="at"/> after moving the day start by <paramref name="shift"/>.</summary>
    public static DateOnly WorkingDate(DateTimeOffset at, TimeSpan shift) =>
        DateOnly.FromDateTime(at.ToLocalTime().DateTime - shift);

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
