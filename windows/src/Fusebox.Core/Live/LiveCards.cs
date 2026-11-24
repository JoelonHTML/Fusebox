using Fusebox.Core.Processes;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Live;

/// <summary>The prefs a Live card needs, read once per render (Core stays free of Prefs).</summary>
public sealed record LiveOptions(
    IReadOnlySet<string> Absolute, bool NetBits = false, bool NetPing = false, bool HasPingHost = true, bool BatteryTime = true,
    double WarnCpu = 85, double WarnTemp = 95, double WarnPing = 100, int CpuCount = 0)
{
    public bool IsAbsolute(Metric m) => Absolute.Contains(Metrics.Id(m));
}

/// <summary>Everything a card header shows (§3.3/§3.4). Level: 0 fine, 1 warn, 2 danger; <see cref="SideIcon"/> is a Segoe Fluent glyph or "".</summary>
public sealed record CardData(string Value, string Unit, string Template, string Side, int SideLevel, string SideIcon, int Level, bool Big)
{
    public static readonly CardData Empty = new("", "", "", "", 0, "", 0, true);
}

/// <summary>One line in the facts block (L/XL) or the detail page. App = the app it names (clickable, icon).</summary>
public sealed record Fact(string Label, string Value, string? App = null);

/// <summary>Card values, thresholds, templates and facts per metric. Pure: unit-tested.</summary>
public static class LiveCards
{
    const double GiB = 1073741824;

    /// <summary>Value 30 pt for CPU/GPU/RAM, 24 pt for the rest; XS 15 pt (12 for NET and I/O).</summary>
    public static double ValueSize(Metric m, bool xs) => xs ? (m is Metric.Net or Metric.Io ? 12 : 15) : m is Metric.Cpu or Metric.Gpu or Metric.Ram ? 30 : 24;

    public static CardData Card(Metric m, LiveSample s, LiveOptions o)
    {
        bool big = m is Metric.Cpu or Metric.Gpu or Metric.Ram;
        switch (m)
        {
            case Metric.Cpu:
            {
                double temp = s.TempC;
                bool abs = o.IsAbsolute(m) && o.CpuCount > 0;
                var (v, u, t) = abs ? (Fmt.One(s.Cpu / 100 * o.CpuCount), "/" + o.CpuCount, "88.8") : (Fmt.Pct(s.Cpu), "", "100%");
                return new(Num(s.Cpu, v), u, t, Fmt.Deg(temp), Fmt.Level(temp, o.WarnTemp, o.WarnTemp + 10), "", Fmt.Level(s.Cpu, o.WarnCpu, o.WarnCpu + 10), big);
            }
            case Metric.Gpu:
                return new(Num(s.Gpu, Fmt.Pct(s.Gpu)), "", "100%", Fmt.Deg(s.GpuTempC), Fmt.Level(s.GpuTempC, o.WarnTemp, o.WarnTemp + 10), "", Fmt.Level(s.Gpu, 90, 98), big);
            case Metric.Ram:
            {
                int lvl = s.Pressure >= 4 ? 2 : s.Pressure == 2 ? 1 : 0;
                return o.IsAbsolute(m)
                    ? new(Num(s.RamUsed, Fmt.One(s.RamUsed / GiB)), "GB", "88.8", "", 0, "", lvl, big)
                    : new(Num(s.RamPercent, Fmt.Pct(s.RamPercent)), "", "100%", "", 0, "", lvl, big);
            }
            case Metric.Net:
            {
                var (d, u, unit) = Fmt.RatePair(s.NetIn, s.NetOut, o.NetBits);
                string side = o.NetPing && o.HasPingHost && double.IsFinite(s.PingMs) ? $"{Math.Round(s.PingMs):0} ms" : "";
                return new(double.IsNaN(s.NetIn) ? "" : $"↓{d} ↑{u}", unit, "↓888 ↑888", side, Fmt.Level(s.PingMs, o.WarnPing, o.WarnPing + 10),
                    double.IsNaN(s.WifiDbm) ? "" : "", 0, big);
            }
            case Metric.Ssd:
            {
                double used = s.DiskUsedPercent;
                int lvl = used >= 95 ? 2 : used >= 90 ? 1 : 0;
                return o.IsAbsolute(m)
                    ? new(Num(s.DiskFree, $"{(long)(s.DiskFree / 1e9)}"), "GB", "8888", "", 0, "", lvl, big)
                    : new(Num(used, Fmt.Pct(used)), "", "100%", "", 0, "", lvl, big);
            }
            case Metric.Io:
            {
                var (r, w, unit) = Fmt.RatePair(s.DiskRead, s.DiskWrite, o.NetBits);
                return new(double.IsNaN(s.DiskRead) ? "" : $"R {r} W {w}", unit, "R 888 W 888", "", 0, "", 0, big);
            }
            default:
            {
                if (double.IsNaN(s.Battery)) return new("–", "", "100%", "", 0, "", 0, big);
                int lvl = s.Charging ? 0 : s.Battery <= 10 ? 2 : s.Battery <= 20 ? 1 : 0;
                string time = o.BatteryTime ? Fmt.BatteryTime(s.BatteryMinutes) : "";
                return new(Fmt.Pct(s.Battery), "", "100%", time, 0, BatteryGlyph(s.Battery, s.Charging), lvl, big);
            }
        }
    }

    static string Num(double v, string text) => double.IsFinite(v) ? text : "";

    /// <summary>Segoe Fluent battery glyph: charging, else 0/25/50/75/100 by ≥13/38/63/88 (§3.3).</summary>
    public static string BatteryGlyph(double pct, bool charging) =>
        charging ? "" : pct >= 88 ? "" : pct >= 63 ? "" : pct >= 38 ? "" : pct >= 13 ? "" : "";

    /// <summary>What a graph plots: a fixed scale (percent 100, or RAM/disk size in GB) or NaN = auto (NET/I/O).</summary>
    public static double GraphTop(Metric m, LiveSample s, LiveOptions o) => m switch
    {
        Metric.Ram when o.IsAbsolute(m) => double.IsFinite(s.RamTotal) ? s.RamTotal / GiB : 100,
        Metric.Ssd when o.IsAbsolute(m) => double.IsFinite(s.DiskTotal) ? s.DiskTotal / 1e9 : 100,
        Metric.Net or Metric.Io => double.NaN,
        _ => 100,
    };

    /// <summary>Samples → graph values in the unit <see cref="GraphTop"/> describes. Net/I/O second series = upload/write.</summary>
    public static (List<double> Main, List<double>? Second) Series(Metric m, IReadOnlyList<LiveSample> list, LiveOptions o)
    {
        switch (m)
        {
            case Metric.Net: return (list.Select(s => Z(s.NetIn)).ToList(), list.Select(s => Z(s.NetOut)).ToList());
            case Metric.Io: return (list.Select(s => Z(s.DiskRead)).ToList(), list.Select(s => Z(s.DiskWrite)).ToList());
            case Metric.Ram when o.IsAbsolute(m): return (list.Select(s => s.RamUsed / GiB).ToList(), null);
            case Metric.Ssd when o.IsAbsolute(m): return (list.Select(s => (s.DiskTotal - s.DiskFree) / 1e9).ToList(), null);
            default: return (Metrics.Values(list, m), null);
        }
    }

    static double Z(double v) => double.IsFinite(v) ? Math.Max(v, 0) : 0;

    // ---- text shared by the facts, the hover label and the detail page ----

    /// <summary>One graph point in the metric's own words ("23%", "12.4 GB", "↓8 ↑120 KB/s", "R 1 W 2 MB/s").</summary>
    public static string PointText(Metric m, LiveSample s, LiveOptions o)
    {
        var c = Card(m, s, o);
        return c.Value.Length == 0 ? "–" : c.Unit.Length > 0 && !c.Unit.StartsWith('/') ? $"{c.Value} {c.Unit}" : c.Value + c.Unit;
    }

    /// <summary>Min / average / max text for the detail stat cards, in the unit the graph uses.</summary>
    public static string StatText(Metric m, double v, LiveOptions o)
    {
        if (!double.IsFinite(v)) return "–";
        return m switch
        {
            Metric.Net or Metric.Io => Fmt.Short(v, o.NetBits),
            Metric.Ram or Metric.Ssd when o.IsAbsolute(m) => $"{Fmt.One(v)} GB",
            _ => Fmt.Pct(v),
        };
    }

    /// <summary>"Low · avg · high" value: "12 · 23 · 78%" or "11.9 · 12.2 · 12.8 GB".</summary>
    public static string LowAvgHigh(Metric m, IReadOnlyList<double> v, LiveOptions o)
    {
        var (lo, av, hi) = Metrics.Stats(v);
        if (!double.IsFinite(lo)) return "–";
        if (m is Metric.Net or Metric.Io) return $"{Fmt.Short(lo, o.NetBits)} · {Fmt.Short(av, o.NetBits)} · {Fmt.Short(hi, o.NetBits)}";
        bool gb = m is Metric.Ram or Metric.Ssd && o.IsAbsolute(m);
        string F(double x) => gb ? Fmt.One(x) : ((long)Math.Round(x)).ToString();
        return $"{F(lo)} · {F(av)} · {F(hi)}{(gb ? " GB" : "%")}";
    }

    /// <summary>"14:32 · Xcode" for the last peak of the card's kind today, or null.</summary>
    public static string? LastPeak(Metric m, IReadOnlyList<Peak> today)
    {
        var kinds = KindsOf(m);
        var p = today.Where(x => kinds.Contains(x.Kind)).MaxBy(x => x.Start);
        return p == null ? null : p.App is { Length: > 0 } a ? $"{p.Start:HH:mm} · {a}" : $"{p.Start:HH:mm}";
    }

    /// <summary>Peak kinds a card owns (CPU also owns Heat).</summary>
    public static PeakKind[] KindsOf(Metric m) => m switch
    {
        Metric.Cpu => [PeakKind.Cpu, PeakKind.Temp], Metric.Gpu => [PeakKind.Gpu], Metric.Ram => [PeakKind.Mem], Metric.Io => [PeakKind.Io], _ => [],
    };

    /// <summary>The facts of a card (most important first; L shows 4, XL 9). Apps = the last minute's groups.</summary>
    public static List<Fact> Facts(Metric m, LiveSample s, LiveOptions o, IReadOnlyList<double> shortValues, IReadOnlyList<AppGroup>? apps, IReadOnlyList<Peak> today)
    {
        var f = new List<Fact>();
        var real = apps?.Where(g => g.Kind != AppKind.Windows).ToList();
        void TopApps(Func<AppGroup, double> by, Func<double, string> fmt, int n)
        {
            if (real == null) return;
            foreach (var g in real.OrderByDescending(by).Where(g => by(g) > 0).Take(n)) f.Add(new(g.Name, fmt(by(g)), g.Name));
        }
        void AddPeak() { if (LastPeak(m, today) is { } p) f.Add(new(L("Last peak"), p)); }
        switch (m)
        {
            case Metric.Cpu:
                if (s.Cores.Length > 0) f.Add(new(L("In use"), L("%@ of %lld cores", Fmt.One(s.Cpu / 100 * s.Cores.Length), s.Cores.Length)));
                if (double.IsFinite(s.TempC)) f.Add(new(L("Hottest core"), Fmt.Deg(s.TempC)));
                AddPeak();
                TopApps(g => g.Cpu, Fmt.Pct, 3);
                f.Add(new(L("Low · avg · high"), LowAvgHigh(m, shortValues, o)));
                break;
            case Metric.Gpu:
                if (double.IsFinite(s.GpuTempC)) f.Add(new(L("Temperature"), Fmt.Deg(s.GpuTempC)));
                AddPeak();
                f.Add(new(L("Low · avg · high"), LowAvgHigh(m, shortValues, o)));
                break;
            case Metric.Ram:
                f.Add(new(L("In use"), L("%@ of %lld GB", Fmt.One(s.RamUsed / GiB), (long)Math.Round(s.RamTotal / GiB))));
                f.Add(new(L("Pressure"), s.Pressure >= 4 ? L("critical") : s.Pressure == 2 ? L("high") : L("normal")));
                if (double.IsFinite(s.SwapBytes)) f.Add(new(L("Swap"), $"{Fmt.One(s.SwapBytes / 1e9)} GB"));
                AddPeak();
                TopApps(g => g.Ram, b => $"{Fmt.One(b / GiB)} GB", 3);
                f.Add(new(L("Low · avg · high"), LowAvgHigh(m, shortValues, o)));
                break;
            case Metric.Net:
                f.Add(new(L("Download now"), Fmt.Short(s.NetIn, o.NetBits)));
                f.Add(new(L("Upload now"), Fmt.Short(s.NetOut, o.NetBits)));
                if (o.HasPingHost) f.Add(new(L("Response time"), double.IsFinite(s.PingMs) ? $"{Math.Round(s.PingMs):0} ms" : L("no answer")));
                if (double.IsFinite(s.WifiDbm) && s.WifiDbm < 0) f.Add(new(L("Wi-Fi signal"), WifiSignal(s.WifiDbm)));
                f.Add(new(L("Low · avg · high"), LowAvgHigh(m, shortValues, o)));
                break;
            case Metric.Ssd:
                f.Add(new(L("Free"), $"{(long)(s.DiskFree / 1e9)} GB"));
                f.Add(new(L("Size"), $"{(long)(s.DiskTotal / 1e9)} GB"));
                f.Add(new(L("Used"), $"{(long)((s.DiskTotal - s.DiskFree) / 1e9)} GB · {Fmt.Pct(s.DiskUsedPercent)}"));
                break;
            case Metric.Io:
                f.Add(new(L("Reading now"), Fmt.Short(s.DiskRead, o.NetBits)));
                f.Add(new(L("Writing now"), Fmt.Short(s.DiskWrite, o.NetBits)));
                AddPeak();
                if (double.IsFinite(s.SwapBytes)) f.Add(new(L("Swap"), $"{Fmt.One(s.SwapBytes / 1e9)} GB"));
                break;
            default:
                if (double.IsNaN(s.Battery)) break;
                if (s.Charging) f.Add(new(L("Charging"), double.IsFinite(s.BatteryMinutes) ? L("full in %@", Fmt.Duration((int)s.BatteryMinutes)) : "–"));
                else if (s.OnAc) f.Add(new(L("Power adapter"), L("connected")));
                else f.Add(new(L("On battery"), double.IsFinite(s.BatteryMinutes) ? L("%@ left", Fmt.Duration((int)s.BatteryMinutes)) : "–"));
                if (double.IsFinite(s.BatteryMw) && Math.Abs(s.BatteryMw) >= 100) f.Add(new(s.Charging ? L("Charging at") : L("Using"), $"{Math.Round(Math.Abs(s.BatteryMw) / 1000):0} W"));
                if (double.IsFinite(s.BatteryHealth)) f.Add(new(L("Capacity"), L("%lld%% of new", (long)Math.Round(s.BatteryHealth))));
                f.Add(new(L("Low · avg · high"), LowAvgHigh(m, shortValues, o)));
                break;
        }
        return f;
    }

    /// <summary>"Good · −52 dBm" (real minus sign); quality ≥ −55 Excellent, ≥ −67 Good, ≥ −75 Fair, else Weak.</summary>
    public static string WifiSignal(double dbm) =>
        $"{(dbm >= -55 ? L("Excellent") : dbm >= -67 ? L("Good") : dbm >= -75 ? L("Fair") : L("Weak"))} · −{Math.Abs((long)Math.Round(dbm))} dBm";
}
