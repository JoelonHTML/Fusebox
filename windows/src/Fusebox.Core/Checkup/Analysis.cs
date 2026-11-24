using Fusebox.Core.Processes;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Checkup;

/// <summary>One day of the CSV logs, parsed.</summary>
public sealed record DayData(DateTime Day, List<CsvLog.Minute> Minutes, List<AppsLog.Row> Apps, List<Peak> Peaks, List<LogFile> Files)
{
    public static DayData Empty(DateTime day) => new(day, [], [], [], []);
}

/// <summary>Reads the day files of one folder; results are cached by file modification time and size (§5.3: normally only today is re-read).</summary>
public static class DayLoader
{
    static readonly Dictionary<string, (DateTime Stamp, long Len, object Value)> cache = [];

    public static DayData Load(DateTime day, string? dir = null)
    {
        var files = new List<LogFile>();
        List<T> Read<T>(string prefix, Func<string, List<T>> parse)
        {
            var path = CsvLog.FileFor(day, prefix, dir);
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return [];
                files.Add(new(fi.Name, fi.Length));
                lock (cache)
                    if (cache.TryGetValue(path, out var c) && c.Stamp == fi.LastWriteTimeUtc && c.Len == fi.Length) return (List<T>)c.Value;
                var value = parse(ReadShared(path));
                lock (cache) cache[path] = (fi.LastWriteTimeUtc, fi.Length, value);
                return value;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
        var minutes = Read("meterkast", CsvLog.ReadMinutes);
        var apps = Read("apps", AppsLog.Parse);
        var peaks = Read("peaks", PeakWatch.Parse);
        return new(day.Date, minutes, apps, peaks, files);
    }

    /// <summary>The writer may still hold the file open (append): share it.</summary>
    public static string ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static List<DayData> LoadDays(Period p, DateTime now, string? dir = null) => Periods.Days(p).Select(d => Load(now.Date.AddDays(-d), dir)).ToList();
    public static List<DayData> LoadPrevious(Period p, DateTime now, string? dir = null) => Periods.PrevDays(p).Select(d => Load(now.Date.AddDays(-d), dir)).ToList();
}

/// <summary>Counts of the main log (§5.4 constants) and the score formula (§5.5).</summary>
public sealed record Counts(int Minutes, int Busy, int Pressure, int Hot, int SlowPing, int WeakWifi)
{
    public const double BusyCpu = 85, HotC = 95, SlowMs = 200, WeakDbm = -75;

    public static Counts Of(IEnumerable<CsvLog.Minute> minutes)
    {
        int n = 0, b = 0, p = 0, h = 0, s = 0, w = 0;
        foreach (var m in minutes)
        {
            n++;
            if (m.Cpu >= BusyCpu) b++;
            if (m.Pressure >= 2) p++;
            if (m.Temp >= HotC) h++;
            if (m.Ping >= SlowMs) s++;
            if (m.Wifi < WeakDbm) w++;
        }
        return new(n, b, p, h, s, w);
    }
}

public static class HealthScore
{
    /// <summary>Score 1–10 and reason; null score when nothing was measured.</summary>
    public static (int? Score, string Reason) Of(Counts c, bool diskLow)
    {
        if (c.Minutes == 0) return (null, L("No measurements for this period yet"));
        double n = c.Minutes;
        var parts = new List<(double Penalty, string Text)>
        {
            (Math.Min(4, c.Busy / n * 20), L("%@ of the time the CPU was busy (≥ 85%%)", Fmt.Pct(c.Busy / n * 100))),
            (Math.Min(3, c.Pressure / n * 15), L("%@ of the time memory was (almost) full", Fmt.Pct(c.Pressure / n * 100))),
            (Math.Min(2, c.Hot / n * 10), L("%@ of the time your PC was hot (≥ 95 °C)", Fmt.Pct(c.Hot / n * 100))),
            (diskLow ? 1 : 0, L("your disk is almost full")),
        };
        double sum = parts.Sum(p => p.Penalty);
        int score = Math.Max(1, (int)Math.Round(10 - sum, MidpointRounding.AwayFromZero));
        var top = parts.MaxBy(p => p.Penalty);
        return (score, top.Penalty >= 0.25 ? CheckupFmt.Cap(top.Text) : L("Calm: not busy, memory and temperature fine"));
    }

    /// <summary>≥ 8 good, 5–7 warn, ≤ 4 danger (Look level 0 / 1 / 2); null = secondary.</summary>
    public static int? Level(int? score) => score is not { } s ? null : s >= 8 ? 0 : s >= 5 ? 1 : 2;

    /// <summary>"↑ better than yesterday (6)" / "↓ worse than …" / "= same as …"; empty when a score is missing.</summary>
    public static string Compare(int? score, int? prev, string previousName) =>
        score is not { } s || prev is not { } p ? ""
        : s > p ? L("↑ better than %@ (%lld)", previousName, p) : s < p ? L("↓ worse than %@ (%lld)", previousName, p) : L("= same as %@", previousName);
}

/// <summary>§5.4–5.7: from parsed day data to a <see cref="Report"/>.</summary>
public static class Analysis
{
    const double BusyCpu = Counts.BusyCpu;

    public static Report Build(Period p, DateTime now, IReadOnlyList<DayData> days, IReadOnlyList<DayData> previous, int cores, CrashSummary? crashes = null)
    {
        cores = Math.Max(cores, 1);
        var minutes = days.SelectMany(d => d.Minutes).OrderBy(m => m.Time).ToList();
        var counts = Counts.Of(minutes);
        var r = new Report { Period = p, Made = now, Minutes = counts.Minutes, BusyMin = counts.Busy, PressureMin = counts.Pressure, HotMin = counts.Hot,
            SlowPingMin = counts.SlowPing, WeakWifiMin = counts.WeakWifi, Crashes = crashes ?? CrashSummary.None };
        r.FirstMinute = minutes.Count > 0 ? minutes[0].Time : null;
        r.CpuAvg = minutes.Count > 0 ? minutes.Average(m => m.Cpu) : double.NaN;
        // Disk: last measurement. Total from the log, so it also works for a day without any total column.
        foreach (var m in minutes) if (double.IsFinite(m.DiskFreeGb) && double.IsFinite(m.DiskTotalGb) && m.DiskTotalGb > 0) { r.DiskFreeGb = m.DiskFreeGb; r.DiskTotalGb = m.DiskTotalGb; }
        r.WifiDrops = Runs(minutes.Where(m => m.WifiDrop), m => m.Time, 90).Count;
        (r.Score, r.Reason) = HealthScore.Of(counts, r.DiskLow);
        var prevMinutes = previous.SelectMany(d => d.Minutes).ToList();
        r.PrevScore = prevMinutes.Count > 0 ? HealthScore.Of(Counts.Of(prevMinutes), false).Score : null;

        r.Files = days.SelectMany(d => d.Files).ToList();
        r.Peaks = days.SelectMany(d => d.Peaks).OrderBy(x => x.Start).ToList();
        Apps(r, days, minutes, cores);
        Events(r, minutes);
        return r;
    }

    // ---- apps (§5.4 "Apps aggregation", top lists, busy moments, leaks, runaways, housekeeping) ----

    sealed class Agg { public double Cpu, Mem, PeakCpu, PeakMem; public int N; public bool Windows; }

    static void Apps(Report r, IReadOnlyList<DayData> days, List<CsvLog.Minute> minutes, int cores)
    {
        // The same name several times in one minute: values are added.
        var perMinute = days.SelectMany(d => d.Apps)
            .GroupBy(a => (Minute(a.Time), a.App))
            .Select(g => (T: g.Key.Item1, Name: g.Key.App, Windows: g.Any(a => a.Kind == "macos"), Cpu: g.Sum(a => a.Cpu), Mem: g.Sum(a => a.MemGb)))
            .Where(a => a.Name.Length > 0).ToList();
        var agg = new Dictionary<string, Agg>();
        foreach (var a in perMinute)
        {
            if (!agg.TryGetValue(a.Name, out var g)) agg[a.Name] = g = new() { Windows = a.Windows };
            g.Cpu += a.Cpu; g.Mem += a.Mem; g.N++; g.PeakCpu = Math.Max(g.PeakCpu, a.Cpu); g.PeakMem = Math.Max(g.PeakMem, a.Mem);
        }
        AppStat Stat(KeyValuePair<string, Agg> kv) => new(kv.Key, kv.Value.Cpu / kv.Value.N, kv.Value.Mem / kv.Value.N, kv.Value.N, kv.Value.PeakCpu, kv.Value.PeakMem);
        r.All = agg.Select(Stat).ToList();
        int solid = (int)Math.Min(5, Math.Max(r.Minutes / 20, 1));
        var solids = r.All.Where(a => a.Minutes >= solid).ToList();
        r.TopCpu = solids.OrderByDescending(a => a.Cpu).ThenBy(a => a.Name).Take(5).ToList();
        r.TopRam = solids.OrderByDescending(a => a.MemGb).ThenBy(a => a.Name).Take(5).ToList();
        r.SeenLongest = r.All.Where(a => !agg[a.Name].Windows).OrderByDescending(a => a.Minutes).ThenBy(a => a.Name).Take(5).ToList();

        var own = perMinute.Where(a => !a.Windows).ToList();

        // Busy moments: ≥ 5 consecutive minutes with CPU ≥ 85 %, gaps ≤ 90 s keep the run; newest first, max 5.
        foreach (var run in Runs(minutes.Where(m => m.Cpu >= BusyCpu), m => m.Time, 90).Where(x => x.Count >= 5))
        {
            var from = run[0].Time; var to = run[^1].Time;
            var culprit = own.Where(a => a.T >= from && a.T <= to).GroupBy(a => a.Name).Select(g => (g.Key, Cpu: g.Sum(a => a.Cpu))).OrderByDescending(g => g.Cpu).FirstOrDefault();
            r.Busy.Add(new(from, to.AddSeconds(60), run.Average(m => m.Cpu), culprit.Key));
        }
        r.Busy = r.Busy.OrderByDescending(b => b.Start).Take(5).ToList();

        // Runaways: one full core (100 / cores % of the PC) for ≥ 10 minutes, gaps ≤ 150 s.
        double core = 100.0 / cores;
        foreach (var g in own.Where(a => a.Cpu >= core).GroupBy(a => a.Name))
            foreach (var run in Runs(g, a => a.T, 150).Where(x => x.Count >= 10))
                r.Runaways.Add(new(g.Key, run.Count, run[0].T, run[^1].T.AddSeconds(60)));
        r.Runaways = r.Runaways.OrderByDescending(x => x.Start).ToList();

        r.Leaks = Leaks(own.Select(a => (a.Name, a.T, a.Mem)).ToList());

        // Housekeeping: a known job busy (≥ 25 % of one core) per minute.
        var jobMinutes = new Dictionary<string, SortedSet<DateTime>>();
        foreach (var a in perMinute)
            if (a.Cpu * cores >= Housekeeping.BusyCoreShare && Housekeeping.JobFor(a.Name) is { } job)
                (jobMinutes.TryGetValue(job, out var set) ? set : jobMinutes[job] = []).Add(a.T);
        r.Jobs = jobMinutes.Select(kv => new JobStat(kv.Key, kv.Value.Count)).OrderByDescending(j => j.Minutes).ThenBy(j => j.Job).ToList();
        r.JobRuns = jobMinutes.SelectMany(kv => Runs(kv.Value, t => t, 600).Where(x => x.Count >= 5).Select(x => (kv.Key, x[0], x[^1].AddSeconds(60)))).ToList();
    }

    static DateTime Minute(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);

    /// <summary>Items in time order grouped into runs: consecutive items closer than <paramref name="maxGapSec"/> belong together.</summary>
    public static List<List<T>> Runs<T>(IEnumerable<T> items, Func<T, DateTime> time, double maxGapSec)
    {
        var runs = new List<List<T>>();
        foreach (var it in items.OrderBy(time))
        {
            if (runs.Count > 0 && (time(it) - time(runs[^1][^1])).TotalSeconds <= maxGapSec) runs[^1].Add(it);
            else runs.Add([it]);
        }
        return runs;
    }

    // ---- memory leaks (§5.7.1) ----

    public static List<LeakInfo> Leaks(IReadOnlyList<(string App, DateTime T, double Mem)> points)
    {
        var best = new Dictionary<string, LeakInfo>();
        foreach (var byApp in points.GroupBy(p => p.App))
            foreach (var byDay in byApp.GroupBy(p => p.T.Date))
            {
                var s = byDay.OrderBy(p => p.T).ToList();
                var piece = new List<(DateTime T, double Mem)>();
                void Judge()
                {
                    if (Leak(byApp.Key, piece) is { } l && (!best.TryGetValue(l.App, out var b) || l.To - l.From > b.To - b.From)) best[l.App] = l;
                    piece = [];
                }
                foreach (var p in s)
                {
                    if (piece.Count > 0 && ((p.T - piece[^1].T).TotalMinutes > 30 || p.Mem < 0.6 * piece[^1].Mem)) Judge();
                    piece.Add((p.T, p.Mem));
                }
                Judge();
            }
        return best.Values.OrderByDescending(l => l.To - l.From).ToList();
    }

    static LeakInfo? Leak(string app, List<(DateTime T, double Mem)> s)
    {
        if (s.Count < 30) return null;
        var dur = (s[^1].T - s[0].T).TotalHours;
        if (dur < 3) return null;
        static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
        double from = Median(s.Take(5).Select(p => p.Mem)), to = Median(s.TakeLast(5).Select(p => p.Mem));
        if (from <= 0 || to < 1.5 * from || to - from < 1) return null;
        double peak = 0; int drops = 0;
        foreach (var p in s) { if (p.Mem < 0.85 * peak) drops++; peak = Math.Max(peak, p.Mem); }
        if (drops > 0.1 * s.Count) return null;
        if (Pearson(s.Select(p => (p.T - s[0].T).TotalMinutes).ToArray(), s.Select(p => p.Mem).ToArray()) < 0.7) return null;
        return new(app, from, to, Math.Max(1, (int)Math.Round(dur)), s[0].T, s[^1].T);
    }

    static double Pearson(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < x.Length; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
        return sxx <= 0 || syy <= 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    // ---- log events (§5.6.9), newest first, max 30 ----

    const string GBusy = "", GHot = "", GMem = "", GNet = "", GWifi = "", GDisk = "", GCpu = "", GGear = "";

    static void Events(Report r, List<CsvLog.Minute> minutes)
    {
        var ev = new List<LogEvent>();
        foreach (var b in r.Busy)
            ev.Add(new(b.Start, b.End, 1, GBusy, L("Busy: %@ CPU", Fmt.Pct(b.Cpu)) + (b.App != null ? L(" · mostly %@", b.App) : "")));
        foreach (var run in Runs(minutes.Where(m => m.Temp >= Counts.HotC), m => m.Time, 360))
            ev.Add(new(run[0].Time, run[^1].Time.AddSeconds(60), 2, GHot, L("Hot: up to %lld °C", (long)Math.Round(run.Max(m => m.Temp)))));
        foreach (var run in Runs(minutes.Where(m => m.Pressure >= 2), m => m.Time, 360))
            ev.Add(new(run[0].Time, run[^1].Time.AddSeconds(60), run.Any(m => m.Pressure >= 4) ? 2 : 1, GMem, run.Any(m => m.Pressure >= 4) ? L("Memory full") : L("Memory filling up")));
        foreach (var run in Runs(minutes.Where(m => m.Offline || m.Ping >= Counts.SlowMs), m => m.Time, 360))
        {
            var pings = run.Select(m => m.Ping).Where(double.IsFinite).ToList();
            ev.Add(new(run[0].Time, run[^1].Time.AddSeconds(60), 1, GNet,
                run.All(m => m.Offline) || pings.Count == 0 ? L("Internet down") : L("Internet slow: avg. %lld ms", (long)pings.Average())));
        }
        foreach (var run in Runs(minutes.Where(m => m.WifiDrop), m => m.Time, 360))
            ev.Add(new(run[0].Time, run[^1].Time.AddSeconds(60), 1, GWifi, run.Count > 1 ? L("Wi-Fi dropped for %@", CheckupFmt.Mins(run.Count)) : L("Wi-Fi dropped")));
        foreach (var run in Runs(minutes.Where(m => m.Wifi < Counts.WeakDbm), m => m.Time, 360))
            ev.Add(new(run[0].Time, run[^1].Time.AddSeconds(60), 1, GNet, L("Weak Wi-Fi: around %lld dBm", (long)Math.Round(run.Average(m => m.Wifi)))));
        var low = minutes.FirstOrDefault(m => m.DiskTotalGb > 0 && m.DiskFreeGb / m.DiskTotalGb < 0.10);
        if (low != null) ev.Add(new(low.Time, low.Time, 2, GDisk, L("Disk almost full: %lld GB free", (long)low.DiskFreeGb)));
        foreach (var l in r.Leaks) ev.Add(new(l.Start, l.End, 1, GMem, L("%@ grew from %@ to %@ GB", l.App, Fmt.One(l.From), Fmt.One(l.To))));
        foreach (var x in r.Runaways) ev.Add(new(x.Start, x.End, 1, GCpu, L("%@ used a full processor core for %@", x.App, CheckupFmt.Mins(x.Minutes))));
        foreach (var (job, a, b) in r.JobRuns) ev.Add(new(a, b, 0, GGear, L("%@ — passes by itself", Housekeeping.Tr(job))));
        r.Events = ev.OrderByDescending(e => e.Start).Take(30).ToList();
    }
}
