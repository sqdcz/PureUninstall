using System.Runtime.InteropServices;
using System.Diagnostics;

namespace PureUninstall.Core.Services;

/// <summary>
/// Process-tree helpers. Uninstallers frequently spawn child processes
/// (helper EXEs, elevated brokers, restart managers); waiting only for the
/// root process makes leftover-scanning race against those children.
/// </summary>
public static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
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
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Snapshot of pid -> parent pid across all processes.</summary>
    private static Dictionary<int, int> SnapshotParents()
    {
        var map = new Dictionary<int, int>();
        var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            if (Process32First(snap, ref entry))
            {
                do
                {
                    map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                } while (Process32Next(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    /// <summary>Direct children of the given pids (empty if snapshot failed).</summary>
    public static List<int> GetChildrenOf(IEnumerable<int> pids)
    {
        var wanted = new HashSet<int>(pids);
        var parents = SnapshotParents();
        var children = new List<int>();
        foreach (var (pid, ppid) in parents)
            if (wanted.Contains(ppid) && pid != 0)
                children.Add(pid);
        return children;
    }

    public static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until the root process and all of its (recursive) children have exited.
    /// Polls so children spawned late are still tracked. Returns false on timeout.
    /// </summary>
    public static async Task<bool> WaitForTreeAsync(int rootPid, TimeSpan timeout, CancellationToken ct, Action<string>? log = null)
    {
        var pids = new HashSet<int> { rootPid };
        var sw = Stopwatch.StartNew();
        while (pids.Count > 0 && sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            foreach (var child in GetChildrenOf(pids))
                pids.Add(child);

            await Task.Delay(350, ct);

            foreach (var pid in pids.ToList())
                if (!IsRunning(pid))
                    pids.Remove(pid);

            if (pids.Count > 0)
                log?.Invoke($"等待卸载进程树退出（剩 {pids.Count} 个，{sw.Elapsed.TotalSeconds:F0}s）");
        }
        return pids.Count == 0;
    }
}
