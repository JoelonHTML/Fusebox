using System.Globalization;
using static Fusebox.Core.Strings;

namespace Fusebox.Core;

/// <summary>Mac peak kinds (Pieken.swift), stored ids cpu / gpu / temp / mem / io.</summary>
public enum PeakKind { Cpu, Gpu, Temp, Mem, Io }

public sealed class Peak
{
    public PeakKind Kind { get; init; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public double Top { get; set; }
    public DateTime At { get; set; }
    public int Seconds => (int)Math.Round((End - Start).TotalSeconds);
    /// <summary>Biggest cause (never "Windows" itself), and its value in the peak's unit (%, GB). Null/NaN = unknown.</summary>
    public string? App { get; set; }
    public double AppValue { get; set; } = double.NaN;

    public static string Id(PeakKind k) => k.ToString().ToLowerInvariant();
    public static string Title(PeakKind k) => k switch
    {
        PeakKind.Cpu => "CPU", PeakKind.Gpu => "GPU", PeakKind.Temp => L("Heat"), PeakKind.Mem => L("Memory"), _ => L("Disk"),
    };
}

/// <summary>Follows one kind of measurement; pure (unit-tested). Port of the Mac's PeakTracker: ≥ minLength samples, a gap of 1 survives.</summary>
public sealed class PeakTracker(PeakKind kind, int minLength = 2, int gap = 1)
{
    Peak? open;
    int count, below;

    /// <summary>Running peak, once it is long enough.</summary>
    public Peak? Current => count >= minLength ? open : null;

    /// <summary>Started: first reading above the limit; Closed: a peak that just ended (long enough).</summary>
    public (bool Started, Peak? Closed) Feed(DateTime t, double v, double level, double stepSeconds = 2)
    {
        var step = TimeSpan.FromSeconds(stepSeconds);
        if (v >= level)
        {
            below = 0;
            if (open is not { } p)
            {
                open = new Peak { Kind = kind, Start = t, End = t + step, Top = v, At = t };
                count = 1;
                return (true, null);
            }
            p.End = t + step; count++;
            if (v > p.Top) { p.Top = v; p.At = t; }
            return (false, null);
        }
        if (open is not { } o) return (false, null);
        if (++below <= gap) return (false, null);
        open = null; var n = count; count = 0; below = 0;
        return (false, n >= minLength ? o : null);
    }
}

/// <summary>
/// Peak detection on every sample (§3.9): CPU ≥ 80 %, GPU ≥ 80 %, heat ≥ warnTemp, memory pressure ≥ 2 (one sample is enough),
/// disk I/O ≥ max(50 MB/s, 95th percentile of the last hour, recomputed every 30 samples). Closed peaks pile up in
/// <see cref="Today"/> and raise <see cref="Closed"/>; LiveHub appends them to peaks-YYYY-MM-DD.csv.
/// </summary>
public sealed class PeakWatch
{
    public const double Busy = 80, IoFloor = 50e6;
    public const string CsvHeader = "tijd,eind,soort,hoogte,app,app_waarde\n";
    /// <summary>v1 header names → Mac names (for <see cref="CsvLog.MigrateHeader"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> V1Columns = new Dictionary<string, string>
    {
        ["time"] = "tijd", ["end"] = "eind", ["kind"] = "soort", ["top"] = "hoogte",
    };

    readonly Dictionary<PeakKind, PeakTracker> trackers = new()
    {
        [PeakKind.Cpu] = new(PeakKind.Cpu), [PeakKind.Gpu] = new(PeakKind.Gpu), [PeakKind.Temp] = new(PeakKind.Temp),
        [PeakKind.Mem] = new(PeakKind.Mem, 1), [PeakKind.Io] = new(PeakKind.Io),
    };
    readonly Queue<double> io = new();
    int ioCount;
    double ioLevel = IoFloor;
    DateTime day;

    public List<Peak> Today { get; } = [];
    public event Action<Peak>? Closed;
    /// <summary>Raised on the first sample above a limit (the moment for an app snapshot).</summary>
    public event Action<PeakKind>? Started;

    public void Feed(LiveSample s, double stepSeconds = 2, double warnTemp = 95)
    {
        if (s.Time.Date != day) { day = s.Time.Date; Today.Clear(); }
        Feed(PeakKind.Cpu, s.Time, s.Cpu, Busy, stepSeconds);
        Feed(PeakKind.Gpu, s.Time, s.Gpu, Busy, stepSeconds);
        Feed(PeakKind.Temp, s.Time, s.TempC, warnTemp > 0 ? warnTemp : 95, stepSeconds);
        Feed(PeakKind.Mem, s.Time, s.Pressure, 2, stepSeconds);
        double rw = Metrics.Value(Metric.Io, s);
        if (double.IsFinite(rw))
        {
            io.Enqueue(rw);
            while (io.Count > SampleHistory.RecentCap) io.Dequeue();
            if (++ioCount % 30 == 0) ioLevel = Math.Max(IoFloor, Percentile(io, 0.95));
            Feed(PeakKind.Io, s.Time, rw, ioLevel, stepSeconds);
        }
    }

    void Feed(PeakKind k, DateTime t, double v, double level, double step)
    {
        if (double.IsNaN(v)) return;
        var (started, closed) = trackers[k].Feed(t, v, level, step);
        if (started) Started?.Invoke(k);
        if (closed != null) { Today.Add(closed); Closed?.Invoke(closed); }
    }

    public Peak? Running(PeakKind k) => trackers[k].Current;

    /// <summary>Today's peaks from the CSV after a restart. Call before the first Feed.</summary>
    public void Seed(IEnumerable<Peak> peaks, DateTime now)
    {
        day = now.Date;
        Today.Clear();
        Today.AddRange(peaks.Where(p => p.Start.Date == day));
    }

    static double Percentile(IEnumerable<double> v, double p)
    {
        var a = v.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a[Math.Clamp((int)Math.Ceiling(p * a.Length) - 1, 0, a.Length - 1)];
    }

    public static string CsvLine(Peak p) => string.Join(',', CsvLog.Stamp(p.Start), CsvLog.Stamp(p.End), Peak.Id(p.Kind),
        p.Top.ToString("0.0", CsvLog.Culture), (p.App ?? "").Replace(',', ' '),
        double.IsFinite(p.AppValue) ? p.AppValue.ToString("0.0", CsvLog.Culture) : "") + "\n";

    /// <summary>peaks-…csv back (by column name; v1 names accepted). Unknown kinds and malformed lines are skipped.</summary>
    public static List<Peak> Parse(string csv)
    {
        var t = CsvTable.Parse(csv);
        string Col(string mac) => t.Col(mac) >= 0 ? mac : V1Columns.FirstOrDefault(kv => kv.Value == mac).Key ?? mac;
        var list = new List<Peak>();
        foreach (var r in t.Rows)
        {
            if (!t.Time(r, out var start)) continue;
            var endText = t.Text(r, Col("eind"));
            if (!DateTime.TryParseExact(endText, "yyyy-MM-dd HH:mm:ss", CsvLog.Culture, DateTimeStyles.None, out var end)) continue;
            int k = Array.FindIndex(Enum.GetValues<PeakKind>(), x => Peak.Id(x) == t.Text(r, Col("soort")));
            double top = t.Num(r, Col("hoogte"));
            if (k < 0 || !double.IsFinite(top)) continue;
            var app = t.Text(r, "app");
            list.Add(new Peak { Kind = (PeakKind)k, Start = start, End = end, Top = top, At = start, App = app.Length > 0 ? app : null, AppValue = t.Num(r, "app_waarde") });
        }
        return list;
    }
}
