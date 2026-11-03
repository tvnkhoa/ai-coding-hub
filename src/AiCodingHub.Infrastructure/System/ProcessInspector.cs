using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AiCodingHub.Infrastructure.System;

/// <summary>Process facts the session tracker needs: liveness, parent links, foreground window owner.</summary>
public interface IProcessInspector
{
    /// <summary>True when <paramref name="pid"/> runs and looks like Claude Code.</summary>
    bool IsAlive(int pid);

    /// <summary>pid → parent pid for every running process (a point-in-time snapshot).</summary>
    IReadOnlyDictionary<int, int> ParentMap();

    /// <summary>PID owning the foreground window, or null.</summary>
    int? ForegroundProcessId();
}

/// <summary>Win32 implementation (Toolhelp snapshot + GetForegroundWindow). Best-effort; never throws.</summary>
public sealed class ProcessInspector : IProcessInspector
{
    public bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited)
            {
                return false;
            }

            // Guard against PID reuse: the registry pid is claude.exe (or a node host).
            var name = p.ProcessName;
            return name.Contains("claude", StringComparison.OrdinalIgnoreCase)
                || name.Contains("node", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // not running
        }
        catch (Exception)
        {
            return true; // access denied etc. — it exists
        }
    }

    public IReadOnlyDictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return map;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
            }
            while (Process32NextW(snapshot, ref entry));
        }
        catch (Exception)
        {
            // partial map is fine
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    public int? ForegroundProcessId()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == 0 ? null : (int)pid;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="ancestor"/> is <paramref name="pid"/> or one of its parents.</summary>
    public static bool IsAncestor(IReadOnlyDictionary<int, int> parents, int ancestor, int pid)
    {
        var current = pid;
        for (var depth = 0; depth < 32; depth++)
        {
            if (current == ancestor)
            {
                return true;
            }

            if (!parents.TryGetValue(current, out var parent) || parent == current || parent <= 0)
            {
                return false;
            }
            current = parent;
        }

        return false;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
