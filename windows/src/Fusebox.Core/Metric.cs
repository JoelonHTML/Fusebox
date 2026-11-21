namespace Fusebox.Core;

public enum Metric { Cpu, Gpu, Ram, Net, Ssd, Bat }
public enum Period { Minutes5, Hour, Today }

/// <summary>What each Live card graphs, and the card order from Prefs ("cpu,gpu,ram,net,ssd,bat").</summary>
public static class Metrics
{
    public const string DefaultOrder = "cpu,gpu,ram,net,ssd,bat";

    public static double Value(Metric m, LiveSample s) => m switch
    {
        Metric.Cpu => s.Cpu,
        Metric.Gpu => s.Gpu,
        Metric.Ram => s.RamPercent,
        Metric.Net => double.IsNaN(s.NetIn) ? double.NaN : s.NetIn + (double.IsNaN(s.NetOut) ? 0 : s.NetOut),
        Metric.Ssd => double.IsNaN(s.DiskRead) ? double.NaN : s.DiskRead + (double.IsNaN(s.DiskWrite) ? 0 : s.DiskWrite),
        _ => s.Battery,
    };

    /// <summary>Fixed graph scale, or NaN to scale to the data.</summary>
    public static double Scale(Metric m) => m is Metric.Net or Metric.Ssd ? double.NaN : 100;

    public static string Label(Metric m) => m switch { Metric.Cpu => "CPU", Metric.Gpu => "GPU", Metric.Ram => "RAM", Metric.Net => "NET", Metric.Ssd => "SSD", _ => "BAT" };

    public static List<Metric> Parse(string order)
    {
        var l = new List<Metric>();
        foreach (var p in order.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (Enum.TryParse<Metric>(p, true, out var m) && !l.Contains(m)) l.Add(m);
        return l;
    }

    public static string Join(IEnumerable<Metric> l) => string.Join(',', l.Select(m => m.ToString().ToLowerInvariant()));
}

/// <summary>History of one metric: the last hour (every tick) plus minute averages for today.</summary>
public sealed class MetricSeries
{
    public const int Capacity = 1800;                 // one hour at 2 s
    public History Recent { get; } = new(Capacity);
    readonly List<double> minutes = [];               // today, one average per finished minute
    double sum; int n; long bucket = -1; DateTime day;

    public void Add(DateTime t, double v)
    {
        Recent.Add(v);
        if (double.IsNaN(v)) return;
        long b = t.Ticks / TimeSpan.TicksPerMinute;
        if (t.Date != day) { minutes.Clear(); day = t.Date; sum = 0; n = 0; }
        if (b != bucket) { Flush(); bucket = b; }
        sum += v; n++;
    }

    void Flush() { if (n > 0) minutes.Add(sum / n); sum = 0; n = 0; }

    /// <summary>Seeds today's minute averages from the CSV after a restart. Call before the first Add.</summary>
    public void Seed(DateTime today, IEnumerable<double> minuteAverages) { day = today.Date; minutes.Clear(); minutes.AddRange(minuteAverages); }

    /// <summary>Values oldest first. stepSeconds = current measuring interval (2 or 5).</summary>
    public IReadOnlyList<double> Values(Period p, int stepSeconds)
    {
        if (p == Period.Today)
        {
            var l = new List<double>(minutes);
            if (n > 0) l.Add(sum / n);
            return l;
        }
        int count = (p == Period.Minutes5 ? 300 : 3600) / Math.Max(stepSeconds, 1);
        return Recent.Tail(count).ToList();
    }

    public static (double Min, double Avg, double Max) Stats(IReadOnlyList<double> v)
        => v.Count == 0 ? (double.NaN, double.NaN, double.NaN) : (v.Min(), v.Average(), v.Max());
}
