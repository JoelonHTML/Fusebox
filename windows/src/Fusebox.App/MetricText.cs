using Fusebox.Core;
using Fusebox.Core.Live;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>Glue between the prefs, LiveHub and the pure Live logic in Core (<see cref="LiveCards"/>, <see cref="TrayInfo"/>).</summary>
public static class MetricText
{
    /// <summary>The prefs a card needs, read now (cheap: a handful of dictionary lookups).</summary>
    public static LiveOptions Options() => new(
        Prefs.List("absolute").ToHashSet(), Prefs.Bool("netBits"), Prefs.Bool("netPing"), Prefs.Str("pingHost").Length > 0, Prefs.Bool("batteryTime"),
        Prefs.Double("warnCPU"), Prefs.Double("warnTemp"), Prefs.Double("warnPing"), App.Live.Hardware.EfficiencyCores + App.Live.Hardware.PerformanceCores);

    /// <summary>The enabled metrics in the user's order that exist on this PC (battery only with a battery).</summary>
    public static List<Metric> Enabled(LiveSample? s) =>
        Metrics.Parse(Prefs.Str("metrics")).Where(m => s == null || !double.IsNaN(Metrics.Value(m, s)) || m == Metric.Net).ToList();

    public static string Title(Metric m) => Metrics.Title(m);

    public static string PeriodTitle(TimeRange p) => TimeRanges.Title(p, LiveHub.ShortMinutes);

    /// <summary>Lowercase range title for "Downloaded (1 hour)".</summary>
    public static string PeriodLower(TimeRange p) => PeriodTitle(p).ToLowerInvariant();

    /// <summary>Hover label text for one sample of a series: time, value and (full) the peak cause at that moment.</summary>
    public static (string Time, string Value, string? Cause) Point(Metric m, IReadOnlyList<LiveSample> samples, int i, LiveOptions o, IReadOnlyList<Fusebox.Core.Peak> peaks)
    {
        if (i < 0 || i >= samples.Count) return ("", "", null);
        var s = samples[i];
        var kinds = LiveCards.KindsOf(m);
        var p = peaks.FirstOrDefault(x => kinds.Contains(x.Kind) && s.Time >= x.Start && s.Time <= x.End.AddSeconds(1));
        return (s.Time.ToString("HH:mm"), LiveCards.PointText(m, s, o), p == null ? null : PeakInfo.CauseText(p));
    }
}
