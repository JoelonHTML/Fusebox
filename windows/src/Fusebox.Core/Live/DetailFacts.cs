using Fusebox.Core.Processes;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Live;

/// <summary>Headline of the detail screen: big number, unit, side text and the second line with the other representation (§3.8).</summary>
public sealed record DetailHeadline(string Value, string Unit, string Side, string Second, double BigSize);

/// <summary>The metric-specific cards of the detail screen. Pure; the App only draws them.</summary>
public sealed record DetailCards(List<Fact> Facts, string TopTitle, List<Fact> TopApps, string? UsageText = null, double UsageFraction = double.NaN);

public static class DetailFacts
{
    const double GiB = 1073741824;

    public static DetailHeadline Headline(Metric m, LiveSample s, LiveOptions o)
    {
        var c = LiveCards.Card(m, s, o);
        double size = m is Metric.Net or Metric.Io ? 26 : 44;
        string second;
        switch (m)
        {
            case Metric.Cpu:
                second = o.IsAbsolute(m) || s.Cores.Length == 0 ? Fmt.Pct(s.Cpu) : L("%@ of %lld cores busy", Fmt.One(s.Cpu / 100 * s.Cores.Length), s.Cores.Length);
                break;
            case Metric.Ram:
                second = o.IsAbsolute(m) ? L("%@ of %lld GB", Fmt.Pct(s.RamPercent), (long)Math.Round(s.RamTotal / GiB)) : $"{Fmt.One(s.RamUsed / GiB)} GB";
                break;
            case Metric.Ssd:
                second = o.IsAbsolute(m) ? L("%@ used of %lld GB", Fmt.Pct(s.DiskUsedPercent), (long)(s.DiskTotal / 1e9)) : L("%lld GB free", (long)(s.DiskFree / 1e9));
                break;
            case Metric.Gpu:
                second = double.IsFinite(s.GpuTempC) ? L("temperature %@", Fmt.Deg(s.GpuTempC)) : "";
                break;
            case Metric.Net:
                second = !o.HasPingHost ? L("response time check is off") : L("internet response time");
                break;
            case Metric.Io:
                second = L("reading and writing to the SSD");
                break;
            default:
                second = double.IsNaN(s.Battery) ? L("no battery")
                    : s.Charging ? (double.IsFinite(s.BatteryMinutes) ? L("full in %@", Fmt.Duration((int)s.BatteryMinutes)) : L("charging"))
                    : s.OnAc ? L("on power adapter") : double.IsFinite(s.BatteryMinutes) ? L("%@ left", Fmt.Duration((int)s.BatteryMinutes)) : "";
                break;
        }
        string unit = m == Metric.Ssd && o.IsAbsolute(m) ? L("GB free") : c.Unit;
        return new(c.Value.Length == 0 ? "–" : c.Value, unit, c.Side, second, size);
    }

    /// <summary>Total of a rate series over a period: bytes/s × seconds per point.</summary>
    public static double Total(IReadOnlyList<double> perSecond, double secondsPerPoint) => perSecond.Where(double.IsFinite).Sum() * secondsPerPoint;

    /// <param name="period">Lowercase range title ("5 min", "1 hour", "today").</param>
    /// <param name="samples">The period's samples (oldest first); each covers <paramref name="secondsPerPoint"/>.</param>
    public static DetailCards Cards(Metric m, LiveSample s, LiveOptions o, IReadOnlyList<LiveSample> samples, string period, double secondsPerPoint,
        IReadOnlyList<AppGroup>? apps, TimeSpan uptime, int processCount)
    {
        var real = apps?.Where(g => g.Kind != AppKind.Windows).ToList();
        List<Fact> Top(Func<AppGroup, double> by, Func<double, string> fmt) =>
            real == null ? [] : real.OrderByDescending(by).Where(g => by(g) > 0).Take(5).Select(g => new Fact(g.Name, fmt(by(g)), g.Name)).ToList();
        var f = new List<Fact>();
        double Peak(Func<LiveSample, double> pick) => samples.Select(pick).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max();
        double Sum(Func<LiveSample, double> pick) => samples.Select(pick).Where(double.IsFinite).Sum() * secondsPerPoint;
        switch (m)
        {
            case Metric.Cpu:
                if (double.IsFinite(s.TempC)) f.Add(new(L("Hottest core"), Fmt.Deg(s.TempC)));
                f.Add(new(L("Running since restart"), uptime.TotalDays >= 1 ? $"{(int)uptime.TotalDays} d {uptime.Hours} h" : $"{(int)uptime.TotalHours} h {uptime.Minutes} min"));
                if (processCount > 0) f.Add(new(L("Processes"), processCount.ToString()));
                return new(f, L("Busiest apps (last minute)"), Top(g => g.Cpu, Fmt.Pct));
            case Metric.Gpu:
                f.Add(new(L("Load"), Fmt.Pct(s.Gpu)));
                if (double.IsFinite(s.GpuTempC)) f.Add(new(L("Temperature"), Fmt.Deg(s.GpuTempC)));
                if (double.IsFinite(s.GpuMemDedicated)) f.Add(new(L("In use"), Fmt.Bytes(s.GpuMemDedicated)));
                return new(f, L("Busiest apps (last minute)"), Top(g => g.Gpu, Fmt.Pct));
            case Metric.Ram:
                f.Add(new(L("In use"), $"{Fmt.One(s.RamUsed / GiB)} GB · {Fmt.Pct(s.RamPercent)}"));
                f.Add(new(L("Free"), $"{Fmt.One((s.RamTotal - s.RamUsed) / GiB)} GB · {Fmt.Pct(100 - s.RamPercent)}"));
                f.Add(new(L("Installed"), $"{Math.Round(s.RamTotal / GiB)} GB"));
                f.Add(new(L("Memory pressure"), s.Pressure >= 4 ? L("Critical") : s.Pressure == 2 ? L("High") : L("Normal")));
                if (double.IsFinite(s.SwapBytes)) f.Add(new(L("Swap (memory on the SSD)"), $"{Fmt.One(s.SwapBytes / GiB)} GB"));
                return new(f, L("Biggest apps (last minute)"), Top(g => g.Ram, b => $"{Fmt.One(b / GiB)} GB"));
            case Metric.Net:
                f.Add(new(L("Download now"), Fmt.Short(s.NetIn, o.NetBits)));
                f.Add(new(L("Upload now"), Fmt.Short(s.NetOut, o.NetBits)));
                if (o.HasPingHost) f.Add(new(L("Response time"), double.IsFinite(s.PingMs) ? $"{Math.Round(s.PingMs):0} ms" : L("no answer")));
                f.Add(new(L("Downloaded (%@)", period), Fmt.Bytes(Sum(x => x.NetIn))));
                f.Add(new(L("Uploaded (%@)", period), Fmt.Bytes(Sum(x => x.NetOut))));
                f.Add(new(L("Fastest download"), Fmt.Short(Peak(x => x.NetIn), o.NetBits)));
                f.Add(new(L("Fastest upload"), Fmt.Short(Peak(x => x.NetOut), o.NetBits)));
                if (double.IsFinite(s.WifiDbm) && s.WifiDbm < 0) f.Add(new(L("Wi-Fi signal"), LiveCards.WifiSignal(s.WifiDbm)));
                return new(f, "", []);
            case Metric.Ssd:
                double used = s.DiskTotal - s.DiskFree;
                f.Add(new(L("Size"), $"{(long)(s.DiskTotal / 1e9)} GB"));
                f.Add(new(L("Used"), $"{(long)(used / 1e9)} GB · {Fmt.Pct(s.DiskUsedPercent)}"));
                f.Add(new(L("Free"), $"{(long)(s.DiskFree / 1e9)} GB"));
                f.Add(new(L("Reading now"), Fmt.Short(s.DiskRead, o.NetBits)));
                f.Add(new(L("Writing now"), Fmt.Short(s.DiskWrite, o.NetBits)));
                if (double.IsFinite(s.SwapBytes)) f.Add(new(L("Swap"), $"{Fmt.One(s.SwapBytes / 1e9)} GB"));
                return new(f, "", [], L("%lld GB used · %lld GB free", (long)(used / 1e9), (long)(s.DiskFree / 1e9)), s.DiskUsedPercent / 100);
            case Metric.Io:
                f.Add(new(L("Reading now"), Fmt.Short(s.DiskRead, o.NetBits)));
                f.Add(new(L("Writing now"), Fmt.Short(s.DiskWrite, o.NetBits)));
                f.Add(new(L("Read (%@)", period), Fmt.Bytes(Sum(x => x.DiskRead))));
                f.Add(new(L("Written (%@)", period), Fmt.Bytes(Sum(x => x.DiskWrite))));
                f.Add(new(L("Fastest read"), Fmt.Short(Peak(x => x.DiskRead), o.NetBits)));
                f.Add(new(L("Fastest write"), Fmt.Short(Peak(x => x.DiskWrite), o.NetBits)));
                return new(f, "", []);
            default:
                if (double.IsNaN(s.Battery)) return new(f, "", []);
                f.Add(new(L("Charge"), Fmt.Pct(s.Battery)));
                f.Add(new(L("Power"), s.Charging ? L("Charging") : L("On battery")));
                if (double.IsFinite(s.BatteryMinutes)) f.Add(new(s.Charging ? L("Full in") : L("Time left"), Fmt.Duration((int)s.BatteryMinutes)));
                if (double.IsFinite(s.BatteryHealth)) f.Add(new(L("Capacity"), L("%lld%% of new", (long)Math.Round(s.BatteryHealth))));
                if (double.IsFinite(s.BatteryCycles) && s.BatteryCycles > 0) f.Add(new(L("Charge cycles"), ((long)s.BatteryCycles).ToString()));
                return new(f, "", []);
        }
    }
}
