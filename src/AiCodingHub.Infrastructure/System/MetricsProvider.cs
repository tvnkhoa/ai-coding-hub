using System;
using System.Collections.Generic;
using System.Diagnostics;
using AiCodingHub.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace AiCodingHub.Infrastructure.System;

/// <summary>
/// Samples CPU/RAM of the Claude Code process(es). Matches processes named "claude" (the CLI)
/// and computes CPU% from processor-time deltas between samples. Process identification is
/// heuristic and refined in a later phase (see architecture open-items).
/// </summary>
public sealed class MetricsProvider : IMetricsProvider
{
    private static readonly string[] ProcessNames = { "claude" };

    private readonly TimeProvider _time;
    private readonly ILogger<MetricsProvider> _log;
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> _last = new();

    public MetricsProvider(TimeProvider time, ILogger<MetricsProvider> log)
    {
        _time = time;
        _log = log;
    }

    public (double CpuPercent, double RamMb) Sample()
    {
        double ramMb = 0;
        double cpuPercent = 0;
        var now = _time.GetUtcNow();
        var seen = new HashSet<int>();
        var cpuCount = Math.Max(1, Environment.ProcessorCount);

        foreach (var name in ProcessNames)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Enumerating processes '{Name}' failed", name);
                continue;
            }

            foreach (var p in procs)
            {
                try
                {
                    seen.Add(p.Id);
                    ramMb += p.WorkingSet64 / (1024.0 * 1024.0);

                    var cpuNow = p.TotalProcessorTime;
                    if (_last.TryGetValue(p.Id, out var prev))
                    {
                        var wall = (now - prev.At).TotalMilliseconds;
                        if (wall > 0)
                        {
                            var cpuDeltaMs = (cpuNow - prev.Cpu).TotalMilliseconds;
                            cpuPercent += Math.Clamp(cpuDeltaMs / (wall * cpuCount) * 100.0, 0, 100);
                        }
                    }

                    _last[p.Id] = (cpuNow, now);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Reading process {Pid} failed", p.Id);
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        // Forget processes that have exited.
        var stale = new List<int>();
        foreach (var pid in _last.Keys)
        {
            if (!seen.Contains(pid))
            {
                stale.Add(pid);
            }
        }
        foreach (var pid in stale)
        {
            _last.Remove(pid);
        }

        return (Math.Clamp(cpuPercent, 0, 100), ramMb);
    }
}
