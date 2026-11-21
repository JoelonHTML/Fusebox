using Fusebox.Core;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>Texts for the Live cards, the detail page and the tray panel. English keys = Mac wording (translations apply).</summary>
public static class MetricText
{
    /// <summary>Big value + unit, and small facts (first two always, the rest when the window is wide).</summary>
    public sealed record Card(string Value, string Unit, string[] Facts);

    public static string Title(Metric m) => m switch
    {
        Metric.Cpu => "CPU", Metric.Gpu => "GPU", Metric.Ram => L("Memory (RAM)"), Metric.Net => L("Network"), Metric.Ssd => "SSD", Metric.Io => "I/O", _ => L("Battery"),
    };

    public static string Format(Metric m, double v) => double.IsNaN(v) ? "–" : m is Metric.Net or Metric.Io ? Fmt.Rate(v) : Fmt.Percent(v);

    public static string PeriodTitle(TimeRange p) => TimeRanges.Title(p, LiveHub.ShortMinutes);

    static string Gb(double bytes) => (bytes / 1e9).ToString(bytes < 100e9 ? "0.0" : "0", Culture);
    static string Dash(string s) => s.Length == 0 ? "–" : s;

    public static Card Summary(Metric m, LiveSample s, LiveHub hub)
    {
        var hw = hub.Hardware;
        switch (m)
        {
            case Metric.Cpu:
                int n = s.Cores.Length;
                var cpuFacts = new List<string> { L("%@ of %lld cores busy", (s.Cpu / 100 * n).ToString("0.0", Culture), n) };
                if (hw.CpuName.Length > 0) cpuFacts.Add(hw.CpuName);
                if (hw.EfficiencyCores > 0) cpuFacts.Add(L("Per core · %lld efficiency + %lld performance", hw.EfficiencyCores, hw.PerformanceCores));
                return new(Fmt.Percent(s.Cpu), "", [.. cpuFacts]);
            case Metric.Gpu:
                var gpuFacts = new List<string>();
                if (!double.IsNaN(s.GpuMemDedicated)) gpuFacts.Add($"{L("In use")} {Fmt.Bytes(s.GpuMemDedicated)}");
                if (hw.GpuName.Length > 0) gpuFacts.Add(hw.GpuName);
                if (s.GpuEngines.Length > 0) gpuFacts.Add(string.Join(" · ", s.GpuEngines.Take(3).Select(e => $"{e.Engine} {Fmt.Percent(e.Percent)}")));
                return new(Fmt.Percent(s.Gpu), "", [.. gpuFacts]);
            case Metric.Ram:
                var ramFacts = new List<string> { L("%@ of %lld GB", Fmt.Percent(s.RamPercent), (long)Math.Round(s.RamTotal / 1e9)) };
                if (!double.IsNaN(s.CommitUsed)) ramFacts.Add($"{L("Commit")} {Gb(s.CommitUsed)} / {Gb(s.CommitLimit)} GB");
                return new(Gb(s.RamUsed), "GB", [.. ramFacts]);
            case Metric.Net:
                return new("↓ " + Fmt.Rate(s.NetIn), "", ["↑ " + Fmt.Rate(s.NetOut)]);
            case Metric.Io:
                return new(Fmt.Rate(Metrics.Value(Metric.Io, s)), "", [$"{L("Reading now")} {Fmt.Rate(s.DiskRead)} · {L("Writing now")} {Fmt.Rate(s.DiskWrite)}"]);
            case Metric.Ssd:
                double used = s.DiskTotal - s.DiskFree;
                return new(L("%lld GB free", (long)(s.DiskFree / 1e9)), "",
                    [L("%@ used of %lld GB", Fmt.Percent(100 * used / s.DiskTotal), (long)(s.DiskTotal / 1e9)),
                     $"{L("Reading now")} {Fmt.Rate(s.DiskRead)} · {L("Writing now")} {Fmt.Rate(s.DiskWrite)}"]);
            default:
                var bat = new List<string> { s.Charging ? L("Charging") : s.OnAc ? "On AC power" : L("On battery") };
                if (!double.IsNaN(s.BatteryMinutes)) bat.Add($"{(s.Charging ? L("Full in") : L("Time left"))} {BatteryEta.Duration(s.BatteryMinutes)}");
                if (!double.IsNaN(s.BatteryMw) && Math.Abs(s.BatteryMw) >= 100) bat.Add($"{(s.BatteryMw / 1000).ToString("+0.0;-0.0", Culture)} W");
                return new(Fmt.Percent(s.Battery), "", [.. bat]);
        }
    }

    /// <summary>Extra label/value rows on the detail page.</summary>
    public static List<(string Label, string Value)> Details(Metric m, LiveSample s, LiveHub hub, IReadOnlyList<double> values, string period, int secondsPerPoint)
    {
        var hw = hub.Hardware;
        switch (m)
        {
            case Metric.Cpu:
                var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
                return [(L("Load"), Fmt.Percent(s.Cpu)), (L("Installed"), Dash(hw.CpuName)),
                        (L("Running since restart"), up.TotalHours >= 24 ? $"{(int)up.TotalDays} d {up.Hours} h" : $"{(int)up.TotalHours} h {up.Minutes} min")];
            case Metric.Gpu:
                var g = new List<(string, string)> { (L("Load"), Fmt.Percent(s.Gpu)), ("GPU", Dash(hw.GpuName)),
                    (L("In use"), Fmt.Bytes(s.GpuMemDedicated)), ("Shared memory", Fmt.Bytes(s.GpuMemShared)) };
                g.AddRange(s.GpuEngines.Take(6).Select(e => ($"Engine {e.Engine}", Fmt.Percent(e.Percent))));
                return g;
            case Metric.Ram:
                return [(L("In use"), $"{Gb(s.RamUsed)} GB · {Fmt.Percent(s.RamPercent)}"),
                        (L("Free"), $"{Gb(s.RamTotal - s.RamUsed)} GB · {Fmt.Percent(100 - s.RamPercent)}"),
                        (L("Installed"), $"{Math.Round(s.RamTotal / 1e9)} GB"),
                        ("Commit charge", $"{Gb(s.CommitUsed)} of {Gb(s.CommitLimit)} GB")];
            case Metric.Net:
                return [(L("Download now"), Fmt.Rate(s.NetIn)), (L("Upload now"), Fmt.Rate(s.NetOut)),
                        (L("Downloaded (%@)", period), Fmt.Bytes(values.Sum() * secondsPerPoint)),
                        (L("Fastest download"), Fmt.Rate(values.Count > 0 ? values.Max() : 0))];
            case Metric.Io:
                return [(L("Reading now"), Fmt.Rate(s.DiskRead)), (L("Writing now"), Fmt.Rate(s.DiskWrite))];
            case Metric.Ssd:
                double used = s.DiskTotal - s.DiskFree;
                return [(L("Size"), $"{(long)(s.DiskTotal / 1e9)} GB"), (L("Used"), $"{(long)(used / 1e9)} GB · {Fmt.Percent(100 * used / s.DiskTotal)}"),
                        (L("Free"), $"{(long)(s.DiskFree / 1e9)} GB"), (L("Reading now"), Fmt.Rate(s.DiskRead)), (L("Writing now"), Fmt.Rate(s.DiskWrite))];
            default:
                var b = new List<(string, string)> { (L("Charge"), Fmt.Percent(s.Battery)), (L("Power"), s.Charging ? L("Charging") : s.OnAc ? "On AC power" : L("On battery")),
                    (s.Charging ? L("Full in") : L("Time left"), BatteryEta.Duration(s.BatteryMinutes)) };
                if (!double.IsNaN(s.BatteryMw)) b.Add(("Power flow", $"{(s.BatteryMw / 1000).ToString("+0.0;-0.0", Culture)} W"));
                if (!double.IsNaN(s.BatteryHealth)) b.Add((L("Capacity"), L("%lld%% of new", (long)Math.Round(s.BatteryHealth))));
                if (!double.IsNaN(s.BatteryFull)) b.Add(("Full charge capacity", $"{(s.BatteryFull / 1000).ToString("0.0", Culture)} Wh of {(s.BatteryDesign / 1000).ToString("0.0", Culture)} Wh"));
                b.Add((L("Charge cycles"), double.IsNaN(s.BatteryCycles) ? "–" : ((int)s.BatteryCycles).ToString()));
                return b;
        }
    }
}
