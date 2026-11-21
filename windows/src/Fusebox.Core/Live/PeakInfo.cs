using Fusebox.Core.Processes;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Live;

/// <summary>Peak texts, causes and the detail range of a peak (§3.9). Pure.</summary>
public static class PeakInfo
{
    const double GiB = 1073741824;

    /// <summary>Biggest app for a peak kind (CPU/Heat → CPU %, GPU → GPU %, Memory → GB, Disk: not measured per app). Never "Windows" itself.</summary>
    public static (string Name, double Value)? Cause(PeakKind kind, IReadOnlyList<AppGroup>? apps)
    {
        if (apps == null || kind == PeakKind.Io) return null;
        Func<AppGroup, double> by = kind switch { PeakKind.Gpu => g => g.Gpu, PeakKind.Mem => g => g.Ram / GiB, _ => g => g.Cpu };
        var top = apps.Where(g => g.Kind != AppKind.Windows).MaxBy(by);
        return top != null && by(top) > 0 ? (top.Name, by(top)) : null;
    }

    /// <summary>"8 s" (min "2 s") below 2 minutes, then "5 min".</summary>
    public static string Length(int seconds) => seconds >= 120 ? $"{seconds / 60} min" : $"{Math.Max(seconds, 2)} s";

    /// <summary>The peak's top value in its own unit: "94%", "97°", "high"/"critical", "12 MB/s".</summary>
    public static string Value(Peak p) => p.Kind switch
    {
        PeakKind.Temp => Fmt.Deg(p.Top),
        PeakKind.Mem => p.Top >= 4 ? L("critical") : L("high"),
        PeakKind.Io => Fmt.Rate(p.Top),
        _ => Fmt.Pct(p.Top),
    };

    /// <summary>Detail row value: CPU card prefixes the kind ("CPU 94%", "Heat 97°"), the others show just the value.</summary>
    public static string RowValue(Peak p, bool prefixKind) => prefixKind ? $"{Peak.Title(p.Kind)} {Value(p)}" : Value(p);

    /// <summary>"Peak 14:32 · 94% · Xcode" (tooltip of the peak dot; app only when known).</summary>
    public static string Label(Peak p) =>
        L("Peak %@ · %@", $"{p.At:HH:mm}", Value(p)) + (p.App is { Length: > 0 } a ? " · " + a : "");

    /// <summary>"Xcode 61%" / "Xcode 2.1 GB", or null.</summary>
    public static string? CauseText(Peak p) =>
        p.App is not { Length: > 0 } a ? null : !double.IsFinite(p.AppValue) ? a : p.Kind == PeakKind.Mem ? $"{a} {Fmt.One(p.AppValue)} GB" : $"{a} {Fmt.Pct(p.AppValue)}";

    /// <summary>Range the detail screen starts in when opened from a peak: &lt; short − 10 s → short, &lt; 3590 s → hour, else today.</summary>
    public static TimeRange RangeFor(DateTime peakStart, DateTime now, int shortMinutes)
    {
        double age = (now - peakStart).TotalSeconds;
        return age < shortMinutes * 60 - 10 ? TimeRange.Short : age < 3590 ? TimeRange.Hour : TimeRange.Today;
    }

    /// <summary>A peak in the last 5 minutes of the card's kinds (the dot on XS/S cards): the newest, or null.</summary>
    public static Peak? Recent(Metric m, IReadOnlyList<Peak> today, DateTime now)
    {
        var kinds = LiveCards.KindsOf(m);
        return today.Where(p => kinds.Contains(p.Kind) && (now - p.End).TotalMinutes <= 5).MaxBy(p => p.Start);
    }
}

/// <summary>One sustained-peak notification to show.</summary>
public sealed record PeakNotice(string App, PeakKind Kind, string Title, string Body);

/// <summary>
/// Sustained-peak alert rule (§3.10): once a minute, for every running peak whose cause app is still running: notify when the peak is
/// ≥ minutes × 60 − 10 s old and that app wasn't notified within the cooldown. The caller keeps <paramref name="lastNotified"/>.
/// </summary>
public static class PeakAlerts
{
    public static string Title(PeakKind kind, string app) => kind switch
    {
        PeakKind.Gpu => L("%@ is keeping your GPU busy", app),
        PeakKind.Temp => L("%@ is heating up your PC", app),
        PeakKind.Mem => L("%@ is filling your memory", app),
        PeakKind.Io => L("%@ is keeping your disk busy", app),
        _ => L("%@ is keeping your CPU busy", app),
    };

    /// <summary>"CPU 92% for 1 minute" / "CPU 92% for 3 minutes".</summary>
    public static string Body(Peak p)
    {
        int minutes = Math.Max(1, (int)(p.Seconds / 60.0));
        return minutes == 1 ? L("%@ %@ for 1 minute", Peak.Title(p.Kind), PeakInfo.Value(p)) : L("%@ %@ for %lld minutes", Peak.Title(p.Kind), PeakInfo.Value(p), minutes);
    }

    public static List<PeakNotice> Due(IEnumerable<(Peak Peak, string? App)> running, int minMinutes, int cooldownMinutes, IDictionary<string, DateTime> lastNotified,
        Func<string, bool> isRunning, DateTime now)
    {
        var l = new List<PeakNotice>();
        foreach (var (p, app) in running)
        {
            if (string.IsNullOrEmpty(app) || p.Seconds < Math.Max(minMinutes, 1) * 60 - 10 || !isRunning(app)) continue;
            if (lastNotified.TryGetValue(app, out var t) && (now - t).TotalMinutes < cooldownMinutes) continue;
            lastNotified[app] = now;
            l.Add(new(app, p.Kind, Title(p.Kind, app), Body(p)));
        }
        return l;
    }
}
