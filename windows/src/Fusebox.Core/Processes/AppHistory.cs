namespace Fusebox.Core.Processes;

/// <summary>What the search box does: matches running apps, plus installed apps that are not running. Pure, for tests.</summary>
public static class ProcessSearch
{
    /// <summary>All words of the query must occur in the name (case-insensitive). Empty query matches everything.</summary>
    public static bool Matches(string name, string query)
    {
        foreach (var w in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!name.Contains(w, StringComparison.CurrentCultureIgnoreCase)) return false;
        return true;
    }

    /// <summary>Installed app names that match and are not already shown as running (no query = none).</summary>
    public static List<string> NotRunning(IEnumerable<string> installed, IEnumerable<string> runningNames, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var running = new HashSet<string>(runningNames, StringComparer.CurrentCultureIgnoreCase);
        return installed.Where(n => !running.Contains(n) && Matches(n, query)).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>Per-app usage history kept in memory: the last hour for graphs, plus average and peak since Fusebox started. No disk.</summary>
public sealed class AppHistory
{
    public sealed class Series
    {
        public History Cpu { get; } = new(1800);
        public History Ram { get; } = new(1800);
        public History Gpu { get; } = new(1800);
        public double CpuSum, CpuPeak, RamSum, RamPeak, GpuSum, GpuPeak;
        public int Samples;
        public DateTime LastSeen;
        public double CpuAvg => Samples == 0 ? 0 : CpuSum / Samples;
        public double RamAvg => Samples == 0 ? 0 : RamSum / Samples;
        public double GpuAvg => Samples == 0 ? 0 : GpuSum / Samples;
    }

    readonly Dictionary<string, Series> series = [];

    public Series? Get(string key) => series.GetValueOrDefault(key);

    public void Record(IEnumerable<AppGroup> groups, DateTime now)
    {
        foreach (var g in groups)
        {
            if (!series.TryGetValue(g.Key, out var s)) series[g.Key] = s = new Series();
            s.Cpu.Add(g.Cpu); s.Ram.Add(g.Ram); s.Gpu.Add(g.Gpu);
            s.CpuSum += g.Cpu; s.RamSum += g.Ram; s.GpuSum += g.Gpu; s.Samples++;
            s.CpuPeak = Math.Max(s.CpuPeak, g.Cpu); s.RamPeak = Math.Max(s.RamPeak, g.Ram); s.GpuPeak = Math.Max(s.GpuPeak, g.Gpu);
            s.LastSeen = now;
        }
        foreach (var k in series.Where(kv => now - kv.Value.LastSeen > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList()) series.Remove(k);
    }
}
