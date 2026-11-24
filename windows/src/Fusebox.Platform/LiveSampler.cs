using System.Runtime.InteropServices;
using Fusebox.Core;
using static Fusebox.Platform.Native;

namespace Fusebox.Platform;

/// <summary>Reads one <see cref="LiveSample"/>. Keeps the previous counters to turn totals into rates. Not thread-safe: one caller.</summary>
public sealed unsafe partial class LiveSampler : IDisposable
{
    readonly PdhQuery pdh = new();
    readonly GpuReader gpu;
    readonly DiskReader disk;
    readonly BatteryReader battery = new();
    static HardwareInfo? hardware;

    /// <summary>CPU/GPU names and P/E cores; read once, on first use (registry + one API call).</summary>
    public static HardwareInfo Hardware => hardware ??= CpuInfo.Read();

    public LiveSampler() { gpu = new GpuReader(pdh); disk = new DiskReader(pdh); }
    public void Dispose() { pdh.Dispose(); battery.Dispose(); }

    SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[] prevCpu = [];
    (ulong inB, ulong outB, long ticks) prevNet;

    public LiveSample Sample()
    {
        var s = new LiveSample();
        pdh.Collect();                 // once per tick for GPU + disk
        ReadCpu(s);
        ReadMemory(s);
        ReadNet(s);
        gpu.Read(s);
        disk.Read(s);
        battery.Read(s);
        return s;
    }

    void ReadCpu(LiveSample s)
    {
        int n = Environment.ProcessorCount;   // ponytail: >64 logical CPUs (processor groups) reports group 0 only
        var cur = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[n];
        fixed (SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION* p = cur)
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, p, n * sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION), out _) != 0) return;
        if (prevCpu.Length == n)
        {
            var cores = new double[n];
            double busyAll = 0, totalAll = 0;
            for (int i = 0; i < n; i++)
            {
                // KernelTime includes idle time.
                double total = (cur[i].KernelTime - prevCpu[i].KernelTime) + (cur[i].UserTime - prevCpu[i].UserTime);
                double idle = cur[i].IdleTime - prevCpu[i].IdleTime;
                cores[i] = total > 0 ? Math.Clamp(100 * (total - idle) / total, 0, 100) : 0;
                busyAll += total - idle; totalAll += total;
            }
            s.Cores = cores;
            s.Cpu = totalAll > 0 ? Math.Clamp(100 * busyAll / totalAll, 0, 100) : 0;
        }
        prevCpu = cur;
    }

    static void ReadMemory(LiveSample s)
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (!GlobalMemoryStatusEx(ref m)) return;
        s.RamTotal = m.ullTotalPhys;
        s.RamUsed = m.ullTotalPhys - m.ullAvailPhys;
        var pi = new PERFORMANCE_INFORMATION { cb = (uint)sizeof(PERFORMANCE_INFORMATION) };
        if (GetPerformanceInfo(&pi, pi.cb))
        {
            s.CommitUsed = (double)pi.CommitTotal * pi.PageSize;
            s.CommitLimit = (double)pi.CommitLimit * pi.PageSize;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [LibraryImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(PERFORMANCE_INFORMATION* info, uint size);

    public static int IfRowSize => sizeof(MIB_IF_ROW2);

    void ReadNet(LiveSample s)
    {
        if (GetIfTable2(out var table) != 0) return;
        try
        {
            uint count = *(uint*)table;
            var rows = (MIB_IF_ROW2*)((byte*)table + 8);   // ULONG NumEntries + padding to 8
            ulong inB = 0, outB = 0;
            for (int i = 0; i < count; i++)
            {
                ref var r = ref rows[i];
                bool hardware = (r.InterfaceAndOperStatusFlags & 1) != 0;
                if (!hardware || r.Type == 24 /* loopback */ || r.OperStatus != 1 /* up */) continue;
                inB += r.InOctets;
                outB += r.OutOctets;
            }
            long now = Environment.TickCount64;
            if (prevNet.ticks > 0 && now > prevNet.ticks && inB >= prevNet.inB && outB >= prevNet.outB)
            {
                double sec = (now - prevNet.ticks) / 1000.0;
                s.NetIn = (inB - prevNet.inB) / sec;
                s.NetOut = (outB - prevNet.outB) / sec;
            }
            prevNet = (inB, outB, now);
        }
        finally { FreeMibTable(table); }
    }
}
