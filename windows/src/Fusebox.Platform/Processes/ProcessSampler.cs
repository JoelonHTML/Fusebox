using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fusebox.Core.Processes;
using static Fusebox.Platform.Native;

namespace Fusebox.Platform;

/// <summary>One NtQuerySystemInformation(SystemProcessInformation) call → all processes.</summary>
public sealed unsafe class ProcessReader
{
    byte[] buf = new byte[512 * 1024];

    // SYSTEM_PROCESS_INFORMATION offsets on 64-bit (x64 and arm64 are identical). Checked by the probe (pid 4 must exist).
    const int NextEntry = 0, Threads = 4, WsPrivate = 8, CreateTime = 32, UserTime = 40, KernelTime = 48, ImageNameLen = 56, ImageNameBuf = 64,
              Pid = 80, ParentPid = 88, WorkingSet = 144, ReadTransfer = 232, WriteTransfer = 240;   // I/O bytes, cumulative

    public List<ProcSample> Read()
    {
        int len;
        while (true)
        {
            int status;
            fixed (byte* p = buf) status = NtQuerySystemInformation(SystemProcessInformation, p, buf.Length, out len);
            if (status == STATUS_INFO_LENGTH_MISMATCH) { buf = new byte[Math.Max(len + 64 * 1024, buf.Length * 2)]; continue; }
            if (status != 0) return [];
            break;
        }
        var list = new List<ProcSample>(512);
        fixed (byte* b = buf)
        {
            byte* e = b;
            while (true)
            {
                ushort nameBytes = *(ushort*)(e + ImageNameLen);
                char* nameBuf = *(char**)(e + ImageNameBuf);
                string name = nameBuf == null || nameBytes == 0 ? "" : new string(nameBuf, 0, nameBytes / 2);
                int pid = (int)*(long*)(e + Pid);
                long ws = *(long*)(e + WsPrivate);
                if (ws <= 0) ws = *(long*)(e + WorkingSet);
                if (pid == 0) name = "System Idle Process";
                list.Add(new ProcSample(pid, (int)*(long*)(e + ParentPid), name, *(long*)(e + UserTime) + *(long*)(e + KernelTime),
                    *(long*)(e + CreateTime), ws, *(int*)(e + Threads), *(long*)(e + ReadTransfer), *(long*)(e + WriteTransfer)));
                uint next = *(uint*)(e + NextEntry);
                if (next == 0) break;
                e += next;
            }
        }
        return list;
    }
}

/// <summary>Visible top-level app windows (what Task Manager counts as "Apps"): visible, no owner, not a tool window, not cloaked, has a title.</summary>
public static unsafe class WindowScanner
{
    [ThreadStatic] static List<(IntPtr, int)>? found;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Callback(IntPtr hwnd, IntPtr lParam)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != 0) return 1;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return 1;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return 1;
        if (GetWindowTextLength(hwnd) == 0) return 1;
        GetWindowThreadProcessId(hwnd, out int pid);
        found!.Add((hwnd, pid));
        return 1;
    }

    public static List<(IntPtr Hwnd, int Pid)> Windows()
    {
        found = [];
        EnumWindows(&Callback, 0);
        var r = found; found = null;
        return r;
    }
}

/// <summary>Stop apps: polite WM_CLOSE first, then (after the UI asked the user) TerminateProcess.</summary>
public static class ProcessControl
{
    /// <summary>Posts WM_CLOSE to the windows of these pids. Returns how many windows were asked.</summary>
    public static int RequestClose(IReadOnlyCollection<int> pids)
    {
        int n = 0;
        foreach (var (hwnd, pid) in WindowScanner.Windows())
            if (pids.Contains(pid) && PostMessage(hwnd, WM_CLOSE, 0, 0)) n++;
        return n;
    }

    /// <summary>Brings the first window of these pids to the front (restoring it when minimized). False when none was found.</summary>
    public static bool BringToFront(IReadOnlyCollection<int> pids)
    {
        foreach (var (hwnd, pid) in WindowScanner.Windows())
        {
            if (!pids.Contains(pid)) continue;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);   // SW_RESTORE
            return SetForegroundWindow(hwnd);
        }
        return false;
    }

    public static bool AllExited(IReadOnlyCollection<int> pids)
    {
        foreach (var pid in pids)
        {
            var h = OpenProcess(SYNCHRONIZE, false, pid);
            if (h == 0) continue;   // gone (or no access: treat as gone)
            bool alive = WaitForSingleObject(h, 0) != 0;
            CloseHandle(h);
            if (alive) return false;
        }
        return true;
    }

    /// <summary>Kills every pid; true when none refused.</summary>
    public static bool Terminate(IReadOnlyCollection<int> pids)
    {
        bool ok = true;
        foreach (var pid in pids)
        {
            if (pid <= 4 || pid == Environment.ProcessId) continue;
            var h = OpenProcess(PROCESS_TERMINATE, false, pid);
            if (h == 0) { ok &= !ProcessAlive(pid); continue; }
            ok &= TerminateProcess(h, 1);
            CloseHandle(h);
        }
        return ok;
    }

    static bool ProcessAlive(int pid) => !AllExited([pid]);

    /// <summary>Efficiency mode like Task Manager: EcoQoS (power throttling) plus idle priority; off restores the system default and normal priority.
    /// Returns how many processes accepted it (protected or elevated ones refuse).</summary>
    public static int SetEfficiency(IReadOnlyCollection<int> pids, bool on)
    {
        int ok = 0;
        foreach (var pid in pids)
        {
            if (pid <= 4 || pid == Environment.ProcessId) continue;
            var h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (h == 0) continue;
            // ControlMask 0 = back to the system's own choice.
            var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = on ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0, StateMask = on ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0 };
            if (SetProcessInformation(h, ProcessPowerThrottling, ref st, 12)) { SetPriorityClass(h, on ? IDLE_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS); ok++; }
            CloseHandle(h);
        }
        return ok;
    }

    /// <summary>True when every process we may ask has EcoQoS switched on (false when none could be read).</summary>
    public static bool IsEfficient(IReadOnlyCollection<int> pids)
    {
        int read = 0;
        foreach (var pid in pids)
        {
            if (pid <= 4) continue;
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == 0) continue;
            var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            bool got = GetProcessInformation(h, ProcessPowerThrottling, ref st, 12);
            CloseHandle(h);
            if (!got) continue;
            read++;
            if ((st.ControlMask & st.StateMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) == 0) return false;
        }
        return read > 0;
    }
}

/// <summary>Exe path (cached per pid + create time) and friendly name/company/version from the file's version resource (cached per path).</summary>
public sealed unsafe class AppMeta
{
    readonly Dictionary<(int, long), string?> paths = [];
    static readonly Dictionary<string, (string Name, string? Company, string? Version)> info = new(StringComparer.OrdinalIgnoreCase);

    public string? PathOf(int pid, long createTime)
    {
        if (paths.TryGetValue((pid, createTime), out var p)) return p;
        p = null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != 0)
        {
            char* buf = stackalloc char[1024];
            uint size = 1024;
            if (QueryFullProcessImageName(h, 0, buf, ref size)) p = new string(buf, 0, (int)size);
            CloseHandle(h);
        }
        return paths[(pid, createTime)] = p;
    }

    public void Forget(IReadOnlySet<(int, long)> alive)
    {
        foreach (var k in paths.Keys.Where(k => !alive.Contains(k)).ToList()) paths.Remove(k);
    }

    public static (string Name, string? Company, string? Version) Describe(string? path, string imageName)
    {
        if (path == null) return (AppGrouper.Stem(imageName), null, null);
        lock (info) { if (info.TryGetValue(path, out var hit)) return hit; }
        // The file read stays outside the lock: the UI thread (app page) must not wait while the sampler reads many new exes.
        (string, string?, string?) r;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            string name = !string.IsNullOrWhiteSpace(v.FileDescription) ? v.FileDescription!.Trim()
                : !string.IsNullOrWhiteSpace(v.ProductName) ? v.ProductName!.Trim() : System.IO.Path.GetFileNameWithoutExtension(path);
            r = (name, Clean(v.CompanyName), Clean(v.ProductVersion ?? v.FileVersion));
        }
        catch { r = (System.IO.Path.GetFileNameWithoutExtension(path), null, null); }
        lock (info) return info[path] = r;
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Everything the Processes page needs, once per tick. Not thread-safe: one caller at a time.</summary>
public sealed class ProcessSampler : IDisposable
{
    readonly ProcessReader reader = new();
    readonly CpuTracker cpu = new();
    readonly DiskTracker disk = new();
    readonly AppMeta meta = new();
    readonly PdhQuery pdh = new();
    readonly int gpuCounter;
    bool gpuWarm;

    public ProcessSampler() => gpuCounter = pdh.Add(@"\GPU Engine(*)\Utilization Percentage");

    public ProcessSnapshot Sample()
    {
        var procs = reader.Read();
        int cpus = Environment.ProcessorCount;
        long wall = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
        var cpuMap = cpu.Update(procs, wall, cpus);
        var diskMap = disk.Update(procs, wall);

        Dictionary<int, double> gpuMap = [];
        if (gpuCounter >= 0 && pdh.Collect())
        {
            if (gpuWarm) gpuMap = GpuInstance.PerPid(pdh.Values(gpuCounter));
            gpuWarm = true;
        }

        var windowPids = new HashSet<int>();
        foreach (var (_, pid) in WindowScanner.Windows()) windowPids.Add(pid);

        var createOf = new Dictionary<int, long>(procs.Count);
        foreach (var p in procs) createOf[p.Pid] = p.CreateTime;
        meta.Forget(procs.Select(p => (p.Pid, p.CreateTime)).ToHashSet());

        var groups = AppGrouper.Group(procs, windowPids, pid => meta.PathOf(pid, createOf.GetValueOrDefault(pid)),
            (path, img) => AppMeta.Describe(path, img).Name, cpuMap, gpuMap, Environment.ProcessId, diskMap);

        // "Windows itself" = the leftover: busy CPU (100 − idle) and used memory minus what the apps account for.
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        double used = GlobalMemoryStatusEx(ref mem) ? mem.ullTotalPhys - mem.ullAvailPhys : double.NaN;
        AppGrouper.AddRemainder(groups, cpuMap.TryGetValue(0, out var idle) && cpu.Warm ? 100 - idle : double.NaN, used);

        int self = Environment.ProcessId;
        long ownRam = procs.FirstOrDefault(p => p.Pid == self).WorkingSet;
        return new ProcessSnapshot(groups, procs.Count, cpuMap.GetValueOrDefault(self), ownRam, cpus, DateTime.Now);
    }

    public static (string Name, string? Company, string? Version) Describe(string? path, string imageName) => AppMeta.Describe(path, imageName);

    public void Dispose() => pdh.Dispose();
}
