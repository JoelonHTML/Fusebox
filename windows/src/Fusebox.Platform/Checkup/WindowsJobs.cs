using System.Diagnostics;
using Fusebox.Core.Checkup;

namespace Fusebox.Platform.Checkup;

/// <summary>"What Windows is doing" right now: CPU of the processes behind the housekeeping jobs (Search indexer, OneDrive, Windows Update, Defender), measured over one second.</summary>
public static class WindowsJobs
{
    static readonly string[] Names = ["SearchIndexer", "OneDrive", "TiWorker", "MoUsoCoreWorker", "MsMpEng"];

    /// <summary>The busy jobs' texts (≥ 25 % of a core), in table order. Blocks about a second: background thread only.</summary>
    public static List<string> Busy(int cores)
    {
        var first = Snapshot();
        Thread.Sleep(1000);
        var second = Snapshot();
        var apps = second.Select(kv => (kv.Key, Cpu: first.TryGetValue(kv.Key, out var a) ? Math.Max(kv.Value - a, 0) / 1.0 / Math.Max(cores, 1) * 100 : 0)).ToList();
        return Housekeeping.Busy(apps, cores);
    }

    /// <summary>Total processor seconds per process name.</summary>
    static Dictionary<string, double> Snapshot()
    {
        var map = new Dictionary<string, double>();
        foreach (var n in Names)
            foreach (var p in Process.GetProcessesByName(n))
                using (p)
                    try { map[n] = map.GetValueOrDefault(n) + p.TotalProcessorTime.TotalSeconds; }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return map;
    }
}
