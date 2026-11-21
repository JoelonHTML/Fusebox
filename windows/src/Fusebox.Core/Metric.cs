using static Fusebox.Core.Strings;

namespace Fusebox.Core;

/// <summary>The Live measurements, with the Mac's stored ids (cpu, gpu, ram, net, ssd, io, battery). No fans on Windows.</summary>
public enum Metric { Cpu, Gpu, Ram, Net, Ssd, Io, Battery }

/// <summary>Ids, labels and graph values per metric, and parsing of the "metrics"/"menubarMetrics" prefs.</summary>
public static class Metrics
{
    public const string DefaultOrder = "cpu,gpu,ram,net,ssd,battery";
    public static readonly Metric[] All = Enum.GetValues<Metric>();

    /// <summary>Stored id: cpu, gpu, ram, net, ssd, io, battery.</summary>
    public static string Id(Metric m) => m.ToString().ToLowerInvariant();

    /// <summary>Card label, never translated (CPU, GPU, RAM, NET, SSD, I/O, BAT).</summary>
    public static string Label(Metric m) => m switch
    {
        Metric.Cpu => "CPU", Metric.Gpu => "GPU", Metric.Ram => "RAM", Metric.Net => "NET", Metric.Ssd => "SSD", Metric.Io => "I/O", _ => "BAT",
    };

    /// <summary>Full title (Settings lists), translated.</summary>
    public static string Title(Metric m) => m switch
    {
        Metric.Cpu => L("Processor (CPU)"), Metric.Gpu => L("Graphics chip (GPU)"), Metric.Ram => L("Memory (RAM)"),
        Metric.Net => L("Internet (NET)"), Metric.Ssd => L("Free storage (SSD)"), Metric.Io => L("Storage activity (read/write)"), _ => L("Battery"),
    };

    /// <summary>The number a graph plots: % for CPU/GPU/RAM/SSD(used)/battery, bytes/s for NET (↓+↑) and I/O (R+W). NaN = not on this PC.</summary>
    public static double Value(Metric m, LiveSample s) => m switch
    {
        Metric.Cpu => s.Cpu,
        Metric.Gpu => s.Gpu,
        Metric.Ram => s.RamPercent,
        Metric.Net => double.IsNaN(s.NetIn) ? double.NaN : s.NetIn + (double.IsNaN(s.NetOut) ? 0 : s.NetOut),
        Metric.Ssd => s.DiskUsedPercent,
        Metric.Io => double.IsNaN(s.DiskRead) ? double.NaN : s.DiskRead + (double.IsNaN(s.DiskWrite) ? 0 : s.DiskWrite),
        _ => s.Battery,
    };

    /// <summary>Fixed graph scale (100 %), or NaN = scale to the data (NET, I/O).</summary>
    public static double Scale(Metric m) => m is Metric.Net or Metric.Io ? double.NaN : 100;

    /// <summary>Ids in order, duplicates and unknown ids dropped. Accepts v1's "bat".</summary>
    public static List<Metric> Parse(string order)
    {
        var l = new List<Metric>();
        foreach (var p in order.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var id = p == "bat" ? "battery" : p.ToLowerInvariant();
            int i = Array.FindIndex(All, m => Id(m) == id);
            if (i >= 0 && !l.Contains(All[i])) l.Add(All[i]);
        }
        return l;
    }

    public static string Join(IEnumerable<Metric> l) => string.Join(',', l.Select(Id));

    /// <summary>One metric's values out of a list of samples (oldest first).</summary>
    public static List<double> Values(IEnumerable<LiveSample> samples, Metric m) => samples.Select(s => Value(m, s)).ToList();

    public static (double Min, double Avg, double Max) Stats(IReadOnlyList<double> v)
    {
        var ok = v.Where(double.IsFinite).ToList();
        return ok.Count == 0 ? (double.NaN, double.NaN, double.NaN) : (ok.Min(), ok.Average(), ok.Max());
    }
}

/// <summary>Graph/detail periods (Mac TimeRange): short = "N min" (pref shortMinutes, 1–60), 1 hour, today.</summary>
public enum TimeRange { Short, Hour, Today }

public static class TimeRanges
{
    public static int ShortMinutes(long pref) => (int)Math.Clamp(pref, 1, 60);

    public static string Title(TimeRange r, int shortMinutes) => r switch
    {
        TimeRange.Short => L("%lld min", shortMinutes), TimeRange.Hour => L("1 hour"), _ => L("Today"),
    };
}

/// <summary>
/// Samples in memory (Mac Meter): <see cref="Recent"/> = every sample of the last hour (hard cap 1800), <see cref="Today"/> = minute
/// averages of today (seeded from today's CSV at start) plus the running minute. Not thread-safe: LiveHub feeds it on the UI thread.
/// </summary>
public sealed class SampleHistory
{
    public const double RecentSeconds = 3600;
    public const int RecentCap = 1800;
    readonly List<LiveSample> recent = [], today = [], minute = [];
    DateTime day;

    public IReadOnlyList<LiveSample> Recent => recent;
    public LiveSample? Last => recent.Count > 0 ? recent[^1] : null;

    public void Add(LiveSample s)
    {
        if (s.Time.Date != day) { day = s.Time.Date; today.Clear(); minute.Clear(); }
        if (minute.Count > 0 && MinuteOf(minute[0].Time) != MinuteOf(s.Time)) { today.Add(Average(minute)); minute.Clear(); }
        minute.Add(s);
        recent.Add(s);
        int drop = 0;
        while (drop < recent.Count && (recent.Count - drop > RecentCap || (s.Time - recent[drop].Time).TotalSeconds > RecentSeconds)) drop++;
        if (drop > 0) recent.RemoveRange(0, drop);
    }

    /// <summary>Today's earlier minutes after a restart (from <see cref="CsvLog.ReadSamples"/> + <see cref="MinuteAverages"/>). Call before the first Add.</summary>
    public void Seed(IEnumerable<LiveSample> minuteAverages, DateTime now)
    {
        day = now.Date;
        today.Clear();
        today.AddRange(minuteAverages.Where(m => m.Time.Date == day && MinuteOf(m.Time) < MinuteOf(now)));
    }

    /// <summary>Samples of the last <paramref name="seconds"/>, with half a step margin so the point count doesn't flicker.</summary>
    public List<LiveSample> Since(double seconds, double stepSeconds)
    {
        if (recent.Count == 0) return [];
        var from = recent[^1].Time.AddSeconds(-(seconds + stepSeconds / 2));
        int i = recent.FindIndex(s => s.Time >= from);
        return i < 0 ? [] : recent.GetRange(i, recent.Count - i);
    }

    /// <summary>Today's minute averages plus the running minute's average.</summary>
    public List<LiveSample> Today => minute.Count > 0 ? [.. today, Average(minute)] : [.. today];

    public List<LiveSample> Range(TimeRange r, int shortMinutes, double stepSeconds) => r switch
    {
        TimeRange.Short => Since(ShortSeconds(shortMinutes), stepSeconds),
        TimeRange.Hour => [.. recent],
        _ => Today,
    };

    static double ShortSeconds(int minutes) => TimeRanges.ShortMinutes(minutes) * 60;
    static long MinuteOf(DateTime t) => t.Ticks / TimeSpan.TicksPerMinute;

    /// <summary>Groups samples per clock minute and averages each group (oldest first).</summary>
    public static List<LiveSample> MinuteAverages(IEnumerable<LiveSample> samples) =>
        samples.GroupBy(s => MinuteOf(s.Time)).OrderBy(g => g.Key).Select(g => Average(g.ToList())).ToList();

    /// <summary>
    /// Mac Sample.average: mean of cpu, ram, gpu, net, disk I/O; mean ignoring missing for temperatures and ping; pressure = max;
    /// everything else from the last sample; time = the first sample's.
    /// </summary>
    public static LiveSample Average(IReadOnlyList<LiveSample> l)
    {
        if (l.Count == 0) throw new ArgumentException("empty");
        static double Mean(IEnumerable<double> v) { double s = 0; int n = 0; foreach (var x in v) if (double.IsFinite(x)) { s += x; n++; } return n == 0 ? double.NaN : s / n; }
        var last = l[^1];
        return new LiveSample
        {
            Time = l[0].Time,
            Cpu = Mean(l.Select(s => s.Cpu)), Gpu = Mean(l.Select(s => s.Gpu)),
            RamUsed = Mean(l.Select(s => s.RamUsed)), RamTotal = last.RamTotal, CommitUsed = last.CommitUsed, CommitLimit = last.CommitLimit,
            NetIn = Mean(l.Select(s => s.NetIn)), NetOut = Mean(l.Select(s => s.NetOut)),
            DiskRead = Mean(l.Select(s => s.DiskRead)), DiskWrite = Mean(l.Select(s => s.DiskWrite)),
            DiskFree = last.DiskFree, DiskTotal = last.DiskTotal,
            Battery = last.Battery, OnAc = last.OnAc, Charging = last.Charging, BatteryMw = last.BatteryMw, BatteryMinutes = last.BatteryMinutes,
            BatteryDesign = last.BatteryDesign, BatteryFull = last.BatteryFull, BatteryCycles = last.BatteryCycles,
            Cores = last.Cores, GpuEngines = last.GpuEngines, GpuMemDedicated = last.GpuMemDedicated, GpuMemShared = last.GpuMemShared,
            Pressure = l.Max(s => s.Pressure),
            TempC = Mean(l.Select(s => s.TempC)), GpuTempC = Mean(l.Select(s => s.GpuTempC)), PingMs = Mean(l.Select(s => s.PingMs)),
            SwapBytes = last.SwapBytes, WifiDbm = last.WifiDbm,
        };
    }
}
