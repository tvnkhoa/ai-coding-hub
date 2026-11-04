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

    /// <summary>
    /// Every running <c>claude.exe</c> with its command line and working directory, or null when
    /// they cannot be enumerated (then liveness of sessions missing from the handover registry
    /// falls back to transcript age).
    /// </summary>
    IReadOnlyList<ClaudeProcess>? ClaudeProcesses() => null;
}

/// <summary>A running claude.exe. <see cref="CommandLine"/> / <see cref="Cwd"/> are null when unreadable.</summary>
public sealed record ClaudeProcess(int Pid, string? CommandLine, string? Cwd);

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

    /// <summary>
    /// Toolhelp snapshot filtered to claude.exe, then each process's command line and current
    /// directory read from its PEB (same-user processes need no admin). x64 only.
    /// </summary>
    public IReadOnlyList<ClaudeProcess>? ClaudeProcesses()
    {
        if (!Environment.Is64BitProcess)
        {
            return null;
        }

        var pids = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                if (string.Equals(entry.szExeFile, "claude.exe", StringComparison.OrdinalIgnoreCase))
                {
                    pids.Add((int)entry.th32ProcessID);
                }
            }
            while (Process32NextW(snapshot, ref entry));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return Collect(pids, ReadParameters);
    }

    /// <summary>
    /// The claude.exe list from their pids, or null when there are none: Claude Code may run under
    /// another host (an npm install runs <c>node.exe cli.js</c>), so "no claude.exe" cannot prove
    /// that a session ended.
    /// </summary>
    public static IReadOnlyList<ClaudeProcess>? Collect(IReadOnlyCollection<int> pids, Func<int, (string? CommandLine, string? Cwd)> read)
    {
        if (pids.Count == 0)
        {
            return null;
        }

        var result = new List<ClaudeProcess>(pids.Count);
        foreach (var pid in pids)
        {
            var (commandLine, cwd) = read(pid);
            result.Add(new ClaudeProcess(pid, commandLine, cwd));
        }
        return result;
    }

    private static (string? CommandLine, string? Cwd) ReadParameters(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return (null, null);
        }

        try
        {
            if (IsWow64Process(handle, out var wow64) && wow64)
            {
                return (null, null); // 32-bit target: different PEB layout
            }

            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0
                || pbi.PebBaseAddress == IntPtr.Zero)
            {
                return (null, null);
            }

            // PEB+0x20 → RTL_USER_PROCESS_PARAMETERS; +0x38 CurrentDirectory.DosPath, +0x70 CommandLine.
            if (ReadPointer(handle, pbi.PebBaseAddress + 0x20) is not { } parameters)
            {
                return (null, null);
            }

            var cwd = ReadUnicodeString(handle, parameters + 0x38);
            var commandLine = ReadUnicodeString(handle, parameters + 0x70);
            return (commandLine, cwd);
        }
        catch (Exception)
        {
            return (null, null);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr? ReadPointer(IntPtr process, IntPtr address)
    {
        var buffer = new byte[8];
        if (!ReadProcessMemory(process, address, buffer, buffer.Length, out var read) || read != buffer.Length)
        {
            return null;
        }
        var value = new IntPtr(BitConverter.ToInt64(buffer, 0));
        return value == IntPtr.Zero ? null : value;
    }

    private static string? ReadUnicodeString(IntPtr process, IntPtr address)
    {
        // UNICODE_STRING (x64): ushort Length, ushort MaximumLength, 4 bytes padding, PWSTR Buffer.
        var header = new byte[16];
        if (!ReadProcessMemory(process, address, header, header.Length, out var read) || read != header.Length)
        {
            return null;
        }

        var length = BitConverter.ToUInt16(header, 0);
        var buffer = new IntPtr(BitConverter.ToInt64(header, 8));
        if (length == 0 || buffer == IntPtr.Zero)
        {
            return null;
        }

        var chars = new byte[length];
        if (!ReadProcessMemory(process, buffer, chars, chars.Length, out read) || read != chars.Length)
        {
            return null;
        }
        return global::System.Text.Encoding.Unicode.GetString(chars);
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

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out int read);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
