namespace Fusebox.Core.Processes;

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
