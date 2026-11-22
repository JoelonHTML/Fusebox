using System.Globalization;
using Fusebox.Core.Checkup;
using Fusebox.Core.Live;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Reports;

/// <summary>Everything the Checkup PDF shows; all of it is already known (report, its day minutes, cached startup count). Nothing new is measured.</summary>
public sealed record CheckupPdfInput(Report Report, IReadOnlyList<CheckupTip> Tips, ReportInfo Info, IReadOnlyList<CsvLog.Minute> Minutes, DateTime Now,
    int? StartupApps = null, double? BatteryPct = null, int? BatteryCycles = null);

/// <summary>
/// The Checkup report as a light A4 PDF (translated): score, typical values and what stands out at the top; then statistics, graphs of the
/// period from the minute log (CPU, memory, network, disk free, ping), CPU by hour, peaks and busy moments, top apps, crashes, findings, tips
/// and the PC facts. App names only; every text passes the scrubber.
/// </summary>
public static class CheckupPdf
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F(double v) => !double.IsFinite(v) ? "–" : Math.Abs(v) < 10 ? v.ToString("0.#", Inv) : Math.Round(v).ToString("#,0", Inv);
    static string Gb(double v) => F(v) + " GB";

    /// <summary>"Intel Core i7-1165G7 · 16 GB · Windows 11 Pro 24H2": the PC type, no names.</summary>
    public static string PcLine(ReportInfo i) => string.Join(" · ", new[]
    {
        i.Processor, double.IsFinite(i.MemoryGb) ? $"{Math.Round(i.MemoryGb)} GB" : "", i.Windows,
    }.Where(s => s.Length > 0));

    /// <summary>Bytes/s → divisor and unit for a graph axis.</summary>
    static (double Div, string Unit) RateUnit(double max) => max >= 1e6 ? (1e6, "MB/s") : (1e3, "KB/s");

    public static byte[] Build(CheckupPdfInput x, ScrubIdentity id, bool compress = true)
    {
        var r = x.Report;
        var mins = x.Minutes.OrderBy(m => m.Time).ToList();
        var (from, to) = Periods.Range(r.Period, x.Now);
        if (r.Period != Period.Week && mins.Count > 0)   // one day: the graphs cover the hours the PC was measured, on whole hours
        {
            from = mins[0].Time.Date.AddHours(mins[0].Time.Hour);
            to = new DateTime(Math.Min(to.Ticks, mins[^1].Time.Date.AddHours(mins[^1].Time.Hour + 1).Ticks));
            if ((to - from).TotalHours < 3) to = from.AddHours(3);
        }
        var date = x.Now.ToString("D", Culture) + ", " + x.Now.ToString("HH:mm", Inv);
        var doc = new ReportPdf(L("Checkup report · %@", Periods.Title(r.Period)), string.Join(" · ", new[] { date, PcLine(x.Info) }.Where(s => s.Length > 0)),
            s => Scrubber.Scrub(s, id), compress);

        // ---- score ----
        var cmp = HealthScore.Compare(r.Score, r.PrevScore, Periods.PreviousName(r.Period)).TrimStart('↑', '↓', '=', ' ');
        var detail = string.Join(" · ", new[]
        {
            r.Minutes > 0 ? L("Measured: %@", Fmt.Duration(r.Minutes)) : "", CheckupFmt.Cap(cmp),
        }.Where(s => s.Length > 0));
        doc.Score(r.Score?.ToString(Inv) ?? "–", HealthScore.Level(r.Score) ?? 1, r.Reason, detail);

        var cpu = Summary.Of(mins.Select(m => m.Cpu));
        var ram = Summary.Of(mins.Select(m => m.RamGb));
        var ping = Summary.Of(mins.Where(m => !m.Offline).Select(m => m.Ping));
        var wifi = Summary.Of(mins.Select(m => m.Wifi));
        var down = Summary.Of(mins.Select(m => m.Down));
        var up = Summary.Of(mins.Select(m => m.Up));
        var disk = Summary.Of(mins.Select(m => m.DiskFreeGb));
        var temp = Summary.Of(mins.Select(m => m.Temp));
        double diskFree = double.IsFinite(r.DiskFreeGb) ? r.DiskFreeGb : disk?.Max ?? double.NaN;
        double diskTotal = double.IsFinite(r.DiskTotalGb) ? r.DiskTotalGb : mins.Select(m => m.DiskTotalGb).LastOrDefault(double.IsFinite, double.NaN);
        double ramTotal = x.Info.MemoryGb;
        var hours = Enumerable.Range(0, 24).Select(h => mins.Where(m => m.Time.Hour == h).Select(m => m.Cpu).DefaultIfEmpty(double.NaN).Average()).ToArray();
        int busiest = Enumerable.Range(0, 24).Where(h => double.IsFinite(hours[h])).DefaultIfEmpty(-1).MaxBy(h => h < 0 ? 0 : hours[h]);

        // ---- typical values ----
        var tiles = new List<Tile>();
        string Below(Summary s, Func<double, string> f) => L("95%% of the time below %@", f(s.P95));
        if (cpu != null) tiles.Add(new(L("Processor (CPU)"), F(cpu.Median), "%", Below(cpu, Fmt.Pct)));
        if (ram != null) tiles.Add(new(L("Memory in use"), F(ram.Median), "GB", double.IsFinite(ramTotal) ? L("of %@", Gb(Math.Round(ramTotal))) : Below(ram, Gb)));
        if (down != null)
        {
            var (div, unit) = RateUnit(down.Median);
            tiles.Add(new(L("Network download"), F(down.Median / div), unit, up != null ? L("upload %@", Fmt.Rate(up.Median)) : ""));
        }
        if (ping != null) tiles.Add(new(L("Internet ping"), F(ping.Median), "ms", Below(ping, v => F(v) + " ms")));
        if (wifi != null) tiles.Add(new(L("Wi-Fi signal"), F(wifi.Median), "dBm", Network.WifiMath.Word((int)Math.Round(wifi.Median))));
        if (double.IsFinite(diskFree)) tiles.Add(new(L("Disk free"), F(diskFree), "GB", double.IsFinite(diskTotal) ? L("of %@", Gb(Math.Round(diskTotal))) : ""));
        if (busiest >= 0 && (r.Period != Period.Today || mins.Count >= 120))
            tiles.Add(new(L("Busiest hour"), $"{busiest:00}:00", "", L("CPU %@ on average", Fmt.Pct(hours[busiest]))));
        doc.Glance(L("At a glance"), L("Typical values: the middle of what was measured %@", r.When), tiles);

        // ---- what stands out ----
        doc.StandsOut(L("Stands out"), L("Peaks and unusual values, most important first"), Marks(x, mins), L("Nothing unusual %@.", r.When));

        // ---- statistics ----
        doc.Section(L("Statistics"));
        var rows = new List<string[]>();
        void Row(string name, Summary? s, Func<double, string> f) { if (s != null) rows.Add([name, f(s.Min), f(s.Median), f(s.Avg), f(s.P95), f(s.Max)]); }
        Row(L("CPU (%)"), cpu, F);
        Row(L("Memory in use (GB)"), ram, F);
        Row(L("Download"), down, Fmt.Rate);
        Row(L("Upload"), up, Fmt.Rate);
        Row(L("Internet ping (ms)"), ping, F);
        Row(L("Wi-Fi signal (dBm)"), wifi, F);
        Row(L("Disk free (GB)"), disk, F);
        Row(L("Temperature (°C)"), temp, F);
        doc.Table([L("One value per minute"), L("Lowest"), L("Median"), L("Average"), "95%", L("Highest")], rows, [0.30, 0.14, 0.14, 0.14, 0.14, 0.14]);

        // ---- graphs ----
        if (mins.Count > 1)
        {
            double x1 = (to - from).TotalMinutes;
            var ticks = ReportPdf.TimeTicks(from, to);
            List<(double, double)> Pts(Func<CsvLog.Minute, double> f) => mins.Select(m => ((m.Time - from).TotalMinutes, f(m))).ToList();
            doc.Section(L("Graphs"));
            doc.Chart(L("Processor (CPU)"), 0, x1, ticks, Fmt.Pct, [new(L("CPU"), ReportPdf.Accent, Pts(m => m.Cpu), MarkMax: true)], yMax: 100);
            if (ram != null)
                doc.Chart(L("Memory in use"), 0, x1, ticks, v => F(v) + " GB", [new(L("Memory"), ReportPdf.Accent, Pts(m => m.RamGb), MarkMax: true)],
                    yMax: double.IsFinite(ramTotal) && ramTotal >= ram.Max ? Math.Ceiling(ramTotal) : null);
            if (down != null)
            {
                var (div, unit) = RateUnit(Math.Max(down.Max, up?.Max ?? 0));
                doc.Chart(L("Network"), 0, x1, ticks, v => F(v) + " " + unit,
                    [new(L("Download"), ReportPdf.Accent, Pts(m => m.Down / div), MarkMax: true), new(L("Upload"), Rgb.Gray(0.45), Pts(m => m.Up / div), Fill: false, Dashed: true)]);
            }
            if (ping != null)
                doc.Chart(L("Internet ping"), 0, x1, ticks, v => F(v) + " ms", [new(L("Ping"), ReportPdf.Accent, Pts(m => m.Offline ? double.NaN : m.Ping), MarkMax: true)]);
            if (disk != null)
                doc.Chart(L("Disk free"), 0, x1, ticks, v => F(v) + " GB", [new(L("Free"), ReportPdf.Accent, Pts(m => m.DiskFreeGb))], height: 70,
                    yMax: double.IsFinite(diskTotal) && diskTotal > 0 ? Math.Ceiling(Math.Max(disk.Max * 1.25, diskTotal * 0.25)) : null);
            if (busiest >= 0)
                doc.Bars(L("CPU by hour of the day (average)"), Enumerable.Range(0, 24).Select(h => h.ToString("00", Inv)).ToList(),
                    [(L("CPU"), ReportPdf.Accent.Tint(0.4), hours)], Fmt.Pct, labelEvery: 2, highlight: busiest);
        }

        // ---- peaks and busy moments ----
        if (r.Peaks.Count + r.Busy.Count > 0)
        {
            doc.Section(L("Peaks and busy moments"));
            double x1 = (to - from).TotalMinutes;
            doc.Timeline(L("When"), 0, x1, ReportPdf.TimeTicks(from, to),
                r.Busy.Select(b => ((b.Start - from).TotalMinutes, (b.End - from).TotalMinutes, ReportPdf.Accent)).ToList(),
                r.Peaks.Select(p => ((p.Start - from).TotalMinutes, PeakColor(p.Kind))).ToList(),
                [(L("Busy (CPU ≥ 85%)"), ReportPdf.Accent), (L("Peak"), ReportPdf.Warn)]);
            var list = r.Busy.Select(b => (b.Start, Row: new[] { CheckupFmt.Span(b.Start, b.End), L("Busy"), Fmt.Pct(b.Cpu), b.App ?? "" }))
                .Concat(r.Peaks.Select(p => (p.Start, Row: new[] { CheckupFmt.Span(p.Start, p.End), Peak.Title(p.Kind), PeakInfo.Value(p), p.App ?? "" })))
                .OrderByDescending(t => t.Item1).Take(20).Select(t => t.Row).ToList();
            doc.Table([L("When"), L("What"), L("How high"), L("Mostly")], list, [0.30, 0.18, 0.16, 0.36], "LLRL");
        }

        // ---- top apps ----
        if (r.TopCpu.Count + r.TopRam.Count > 0)
        {
            doc.Section(L("Top apps"));
            doc.HBars(L("By CPU (average share of the whole PC)"), r.TopCpu.Select(a => (a.Name, a.Cpu, Fmt.Pct(a.Cpu))).ToList(), ReportPdf.Accent);
            doc.HBars(L("By memory (average)"), r.TopRam.Select(a => (a.Name, a.MemGb, Gb(a.MemGb))).ToList(), ReportPdf.Accent.Tint(0.6));
        }

        // ---- crashes ----
        var k = r.Crashes;
        if (k.Any)
        {
            doc.Section(L("Crashes and reports from Windows"));
            doc.Table([L("App"), L("Crashed"), L("Stopped responding")],
                k.Apps.Select(a => new[] { a.App + (a.Loop ? " " + L("(%lld× in one hour)", a.LoopCrashes) : ""), a.Crashes > 0 ? $"{a.Crashes}×" : "–", a.Hangs > 0 ? $"{a.Hangs}×" : "–" }).ToList(),
                [0.6, 0.2, 0.2]);
            if (k.BlueScreens > 0) doc.Bullet(L("Blue screens: %lld", k.BlueScreens), ReportPdf.Danger);
            if (k.UnexpectedShutdowns > 0) doc.Bullet(L("Unexpected shutdowns: %lld", k.UnexpectedShutdowns), ReportPdf.Danger);
            if (k.OutOfMemory > 0) doc.Bullet(L("Out of memory warnings: %lld", k.OutOfMemory), ReportPdf.Warn);
        }

        // ---- findings and housekeeping ----
        if (r.Leaks.Count + r.Runaways.Count + r.Jobs.Count > 0)
        {
            doc.Section(L("Findings"));
            foreach (var l in r.Leaks) doc.Bullet(L("%@ grew from %@ to %@ GB in %lld h", l.App, Fmt.One(l.From), Fmt.One(l.To), l.Hours), ReportPdf.Warn, CheckupFmt.Span(l.Start, l.End));
            foreach (var w in r.Runaways) doc.Bullet(L("%@ used a full processor core for %@", w.App, CheckupFmt.Mins(w.Minutes)), ReportPdf.Warn, CheckupFmt.Span(w.Start, w.End));
            foreach (var j in r.Jobs) doc.Bullet(L("Windows housekeeping: %@ for %@", j.Job, Fmt.Duration(j.Minutes)), ReportPdf.Accent);
        }

        // ---- tips ----
        var tips = x.Tips.Where(t => t.Id != "login").ToList();
        if (tips.Count > 0)
        {
            doc.Section(L("Tips"));
            foreach (var t in tips) doc.Bullet(t.Text.Replace('\n', ' '), t.Serious ? ReportPdf.Warn : ReportPdf.Accent);
        }

        // ---- this PC ----
        var i = x.Info;
        var facts = new List<(string, string)>();
        if (i.Processor.Length > 0) facts.Add((L("Processor"), i.Processor));
        if (double.IsFinite(i.MemoryGb)) facts.Add((L("Memory"), $"{Math.Round(i.MemoryGb)} GB"));
        if (i.Windows.Length > 0) facts.Add(("Windows", i.Windows));
        if (i.UptimeMinutes >= 0) facts.Add((L("On for"), Fmt.Duration(i.UptimeMinutes)));
        if (double.IsFinite(i.DiskTotalGb)) facts.Add((L("Disk"), L("%@ GB, %@ GB free", F(Math.Round(i.DiskTotalGb)), F(Math.Round(i.DiskFreeGb)))));
        if (i.StabilityIndex is { } si) facts.Add((L("Stability index"), L("%@ of 10", Fmt.One(si))));
        if (x.BatteryPct is { } bp) facts.Add((L("Battery health"), L("%@ of original capacity", Fmt.Pct(bp)) + (x.BatteryCycles is { } cy ? " · " + L("%lld cycles", cy) : "")));
        if (x.StartupApps is { } sa) facts.Add((L("Startup apps"), sa.ToString(Inv)));
        if (facts.Count > 0) { doc.Section(L("This PC")); doc.Facts(facts); }

        return doc.Save(L("Fusebox · Checkup report · %@", x.Now.ToString("d MMM yyyy", Culture)),
            L("Contains no Wi-Fi name, network addresses, computer name or user name."), L("Checkup report"));
    }

    static Rgb PeakColor(PeakKind k) => k switch { PeakKind.Mem or PeakKind.Temp => ReportPdf.Danger, _ => ReportPdf.Warn };

    /// <summary>The "Stands out" lines: biggest problems first (level 2, then 1, then plain notes).</summary>
    public static List<Mark> Marks(CheckupPdfInput x, IReadOnlyList<CsvLog.Minute> mins)
    {
        var r = x.Report;
        var m = new List<Mark>();
        int Lvl(int minutes, int warn = 1) => minutes * 10 >= Math.Max(r.Minutes, 1) ? 2 : warn;

        // highest CPU moment, with the app when a peak knows it
        var top = mins.Where(v => double.IsFinite(v.Cpu)).DefaultIfEmpty().MaxBy(v => v?.Cpu ?? 0);
        var cpuPeak = r.Peaks.Where(p => p.Kind == PeakKind.Cpu).MaxBy(p => p.Top);
        if (cpuPeak != null)
            m.Add(new(cpuPeak.Top >= 95 ? 1 : 0, L("Highest CPU: %@ on %@", Fmt.Pct(cpuPeak.Top), CheckupFmt.Day(cpuPeak.At == default ? cpuPeak.Start : cpuPeak.At))
                + (cpuPeak.App is { Length: > 0 } a ? " · " + L("mostly %@", a) : ""), L("Peak of %@", PeakInfo.Length(cpuPeak.Seconds))));
        else if (top != null && top.Cpu >= 50)
            m.Add(new(0, L("Highest CPU: %@ on %@", Fmt.Pct(top.Cpu), CheckupFmt.Day(top.Time)), L("Average of that minute")));
        if (r.Peaks.Count > 0)
            m.Add(new(r.Peaks.Count >= 10 ? 1 : 0, (r.Peaks.Count == 1 ? L("1 peak %@", r.When) : L("%lld peaks %@", r.Peaks.Count, r.When)) + ": "
                + string.Join(", ", r.Peaks.GroupBy(p => p.Kind).OrderBy(g => g.Key).Select(g => $"{Peak.Title(g.Key)} {g.Count()}"))));
        if (r.BusyMin > 0) m.Add(new(Lvl(r.BusyMin), L("CPU busy (85%% or more) for %@", Fmt.Duration(r.BusyMin)),
            r.Busy.FirstOrDefault(b => b.App != null) is { } bm ? L("Last time %@, mostly %@", CheckupFmt.Span(bm.Start, bm.End), bm.App!) : ""));
        if (r.PressureMin > 0) m.Add(new(Lvl(r.PressureMin), L("Memory (almost) full for %@", Fmt.Duration(r.PressureMin))));
        if (r.HotMin > 0) m.Add(new(2, L("Hot (95 °C or more) for %@", Fmt.Duration(r.HotMin))));
        if (r.DiskLow) m.Add(new(2, L("Disk almost full: %@ GB free of %@ GB", F(Math.Round(r.DiskFreeGb)), F(Math.Round(r.DiskTotalGb)))));
        var k = r.Crashes;
        if (k.BlueScreens > 0) m.Add(new(2, L("Blue screens: %lld", k.BlueScreens)));
        if (k.UnexpectedShutdowns > 0) m.Add(new(2, L("Unexpected shutdowns: %lld", k.UnexpectedShutdowns)));
        if (k.OutOfMemory > 0) m.Add(new(2, L("Out of memory warnings: %lld", k.OutOfMemory)));
        if (k.Crashed > 0)
        {
            var worst = k.Apps.MaxBy(a => a.Crashes)!;
            m.Add(new(k.Apps.Any(a => a.Loop) ? 2 : 1, k.Crashed == 1 ? L("1 app crashed") : L("%lld apps crashed", k.Crashed), L("Most: %@ (%lld×)", worst.App, worst.Crashes)));
        }
        if (k.Hung > 0) m.Add(new(1, k.Hung == 1 ? L("1 app stopped responding") : L("%lld apps stopped responding", k.Hung)));
        foreach (var l in r.Leaks) m.Add(new(1, L("%@ grew from %@ to %@ GB in %lld h", l.App, Fmt.One(l.From), Fmt.One(l.To), l.Hours)));
        foreach (var g in r.Runaways.GroupBy(w => w.App)) m.Add(new(1, L("%@ used a full processor core for %@", g.Key, CheckupFmt.Mins(g.Sum(w => w.Minutes)))));
        if (r.SlowPingMin > 0) m.Add(new(1, L("Slow internet (ping 200 ms or more) for %@", Fmt.Duration(r.SlowPingMin))));
        if (r.WifiDrops > 0) m.Add(new(r.WifiDrops >= 5 ? 2 : 1, L("Wi-Fi dropped %lld×", r.WifiDrops)));
        if (r.WeakWifiMin > 0) m.Add(new(Lvl(r.WeakWifiMin), L("Weak Wi-Fi (below −75 dBm) for %@", Fmt.Duration(r.WeakWifiMin))));
        int offline = mins.Count(v => v.Offline);
        if (offline > 0) m.Add(new(offline >= 10 ? 1 : 0, L("No internet for %@", Fmt.Duration(offline))));
        if (x.BatteryPct is < 80) m.Add(new(1, L("Battery worn: %@ of original capacity", Fmt.Pct(x.BatteryPct.Value))));
        if (r.Score is { } s && r.PrevScore is { } p && s < p) m.Add(new(1, L("The score went down from %lld to %lld", p, s)));
        return m.OrderByDescending(v => v.Level).Take(14).ToList();
    }
}
