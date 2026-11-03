using System;
using System.Runtime.InteropServices;
using AiCodingHub.Core.Abstractions;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace AiCodingHub.Infrastructure.System;

/// <summary>
/// Samples total system CPU (via <c>GetSystemTimes</c> deltas) and used RAM (via
/// <c>GlobalMemoryStatusEx</c>). CPU is a delta measurement, so the first sample after
/// construction returns 0 until there is a previous reading to diff against.
/// <para>
/// Uses <c>GetSystemTimes</c> rather than a <c>PerformanceCounter</c> deliberately: the PDH
/// counter infrastructure pulls a large amount of memory into the process, which conflicts
/// with the HUD's small-footprint goal.
/// </para>
/// </summary>
public sealed class SystemProvider : ISystemProvider
{
    private ulong _prevIdle;
    private ulong _prevBusy;
    private bool _hasPrev;

    public (double CpuPercent, double RamUsedMb) Sample()
    {
        var cpu = SampleCpu();

        double ramUsedMb = 0;
        var status = new MemoryStatusEx();
        if (GlobalMemoryStatusEx(status))
        {
            var usedBytes = status.ullTotalPhys - status.ullAvailPhys;
            ramUsedMb = usedBytes / (1024.0 * 1024.0);
        }

        return (cpu, ramUsedMb);
    }

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            return 0;
        }

        // kernelTime includes idle; busy = (kernel + user) - idle.
        var idle = ToUInt64(idleFt);
        var total = ToUInt64(kernelFt) + ToUInt64(userFt);
        var busy = total >= idle ? total - idle : 0;

        double cpu = 0;
        if (_hasPrev)
        {
            var idleDelta = idle - _prevIdle;
            var busyDelta = busy - _prevBusy;
            var totalDelta = idleDelta + busyDelta;
            if (totalDelta > 0)
            {
                cpu = Math.Clamp(busyDelta * 100.0 / totalDelta, 0, 100);
            }
        }

        _prevIdle = idle;
        _prevBusy = busy;
        _hasPrev = true;
        return cpu;
    }

    private static ulong ToUInt64(FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
