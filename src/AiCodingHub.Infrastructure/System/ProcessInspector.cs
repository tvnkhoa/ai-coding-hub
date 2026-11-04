using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    /// <summary>
    /// Parent links, the claude.exe list and liveness from one point-in-time pass (the session
    /// poll calls this once instead of <see cref="ParentMap"/> + <see cref="ClaudeProcesses"/>).
    /// </summary>
    ProcessSnapshot Snapshot() => new(ParentMap(), ClaudeProcesses(), null);
}

/// <summary>A running claude.exe. <see cref="CommandLine"/> / <see cref="Cwd"/> are null when unreadable.</summary>
public sealed record ClaudeProcess(int Pid, string? CommandLine, string? Cwd);

/// <summary>
/// One process pass. <see cref="Live"/> holds the pids that run claude/node (the
/// <see cref="IProcessInspector.IsAlive"/> rule); null when unknown, then callers ask IsAlive.
/// </summary>
public sealed record ProcessSnapshot(
    IReadOnlyDictionary<int, int> Parents,
    IReadOnlyList<ClaudeProcess>? Claude,
    IReadOnlySet<int>? Live)
{
    public bool IsAlive(int pid, Func<int, bool> fallback) => Live is { } live ? live.Contains(pid) : fallback(pid);
}

/// <summary>
/// claude.exe command line + cwd by (pid, creation time): the PEB is read once per process, and a
/// reused pid (new creation time) is read again. Pids missing from a pass are evicted. Thread-safe.
/// The cwd is cached too: a live check (4 Nov) found claude.exe keeps its start cwd.
/// </summary>
public sealed class ProcessParameterCache
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (long Created, string? CommandLine, string? Cwd)> _byPid = new();

    public int Count
    {
        get { lock (_gate) { return _byPid.Count; } }
    }

    /// <summary>
    /// The claude.exe list (see <see cref="ProcessInspector.Collect"/>). <paramref name="created"/>
    /// returns the creation time or null (then nothing is cached for that pid); unreadable results
    /// are not cached.
    /// </summary>
    public IReadOnlyList<ClaudeProcess>? Collect(
        IReadOnlyCollection<int> pids,
        Func<int, long?> created,
        Func<int, (string? CommandLine, string? Cwd)> read)
    {
        lock (_gate)
        {
            var present = new HashSet<int>(pids);
            foreach (var gone in _byPid.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _byPid.Remove(gone);
            }

            return ProcessInspector.Collect(pids, pid =>
            {
                var at = created(pid);
                if (at is { } t && _byPid.TryGetValue(pid, out var hit) && hit.Created == t)
                {
                    return (hit.CommandLine, hit.Cwd);
                }

                var value = read(pid);
                if (at is { } t2 && (value.CommandLine is not null || value.Cwd is not null))
                {
                    _byPid[pid] = (t2, value.CommandLine, value.Cwd);
                }
                else
                {
                    _byPid.Remove(pid);
                }
                return value;
            });
        }
    }
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

    private readonly ProcessParameterCache _parameters = new();

    public IReadOnlyDictionary<int, int> ParentMap() => Walk().Parents;

    /// <summary>
    /// One Toolhelp pass: parent links, claude/node pids, and the claude.exe list with command line
    /// and cwd from the PEB (cached per pid + creation time). Never throws.
    /// </summary>
    public ProcessSnapshot Snapshot()
    {
        var (parents, claudePids, live, complete) = Walk();
        IReadOnlyList<ClaudeProcess>? claude = null;
        if (complete && Environment.Is64BitProcess)
        {
            try
            {
                claude = _parameters.Collect(claudePids, CreationTime, ReadParameters);
            }
            catch (Exception)
            {
                claude = null;
            }
        }
        return new ProcessSnapshot(parents, claude, complete ? live : null);
    }

    /// <summary>Toolhelp walk; <c>Complete</c> is false when it failed (the parent map may be partial).</summary>
    private static (Dictionary<int, int> Parents, List<int> ClaudePids, HashSet<int> Live, bool Complete) Walk()
    {
        var map = new Dictionary<int, int>();
        var claude = new List<int>();
        var live = new HashSet<int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return (map, claude, live, false);
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return (map, claude, live, false);
            }

            do
            {
                var pid = (int)entry.th32ProcessID;
                map[pid] = (int)entry.th32ParentProcessID;
                var exe = entry.szExeFile ?? "";
                if (string.Equals(exe, "claude.exe", StringComparison.OrdinalIgnoreCase))
                {
                    claude.Add(pid);
                }

                // Same rule as IsAlive (guards against pid reuse).
                if (exe.Contains("claude", StringComparison.OrdinalIgnoreCase)
                    || exe.Contains("node", StringComparison.OrdinalIgnoreCase))
                {
                    live.Add(pid);
                }
            }
            while (Process32NextW(snapshot, ref entry));
        }
        catch (Exception)
        {
            return (map, claude, live, false);
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return (map, claude, live, true);
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
    /// Every running claude.exe with its command line and current directory read from its PEB
    /// (same-user processes need no admin). x64 only.
    /// </summary>
    public IReadOnlyList<ClaudeProcess>? ClaudeProcesses() => Snapshot().Claude;

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

    /// <summary>Process creation time (FILETIME), or null when the process cannot be opened.</summary>
    private static long? CreationTime(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GetProcessTimes(handle, out var created, out _, out _, out _) ? created : null;
        }
        finally
        {
            CloseHandle(handle);
        }
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
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

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
