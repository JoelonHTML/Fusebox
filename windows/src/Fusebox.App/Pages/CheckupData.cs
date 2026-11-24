using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Core.Processes;
using Fusebox.Platform.Checkup;

namespace Fusebox.App.Pages;

/// <summary>One finished analysis: the report, its tips and when it was made.</summary>
public sealed record CheckupBundle(Report Report, IReadOnlyList<CheckupTip> Tips, DateTime Made);

/// <summary>
/// Checkup's data side (§5.3): the per-period report cache (the page shows the cached report instantly and reloads in the background),
/// the Event Log crash list (cached 5 minutes) and the gist for the tray flyout. Nothing here runs on a timer.
/// </summary>
public static class CheckupData
{
    static readonly Dictionary<Period, CheckupBundle> cache = [];
    static List<EventInfo>? crashEvents;
    static DateTime crashEventsAt = DateTime.MinValue;

    /// <summary>The last report made for a period (any age), or null.</summary>
    public static CheckupBundle? Cached(Period p) => cache.GetValueOrDefault(p);

    /// <summary>
    /// Reads the day files and the Event Log off the UI thread and builds report + tips. Call on the UI thread (it looks at the running apps
    /// and the battery first). Throws OperationCanceledException when cancelled.
    /// </summary>
    public static async Task<CheckupBundle> LoadAsync(Period p, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var stop = (App.Live.Apps ?? []).Where(g => g.CanStop && g.Kind != AppKind.Windows).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var last = App.Live.Last;
        BatteryHealth? battery = last != null && double.IsFinite(last.BatteryHealth) ? new((int)Math.Round(last.BatteryHealth), double.IsFinite(last.BatteryCycles) ? (int)last.BatteryCycles : null) : null;
        int restartDays = Prefs.Int("restartDays");
        var b = await Task.Run(() =>
        {
            var days = DayLoader.LoadDays(p, now);
            var prev = DayLoader.LoadPrevious(p, now);
            ct.ThrowIfCancellationRequested();
            var (from, to) = Periods.Range(p, now);
            var crashes = CrashAnalysis.Build(CrashEvents(), from, to);
            var report = Analysis.Build(p, now, days, prev, Environment.ProcessorCount, crashes);
            var ctx = new TipContext
            {
                UptimeDays = CheckupReaderSafe(() => CheckupReader.UptimeDays()), RestartDays = restartDays,
                PendingReboot = CheckupReaderSafe(() => CheckupReader.PendingReboot()),
                LastUpdate = CheckupReaderSafe<DateTime?>(() => CheckupReader.LastUpdate()),
                Battery = battery, Stoppable = stop.Contains,
            };
            return new CheckupBundle(report, Tips.Build(report, ctx), DateTime.Now);
        }, ct);
        lock (cache) cache[p] = b;
        return b;
    }

    static T? CheckupReaderSafe<T>(Func<T> f)
    {
        try { return f(); } catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return default; }
    }

    /// <summary>Event Log: the last 30 days of crashes, cached 5 minutes (the query takes a moment on a busy log). Empty on failure.</summary>
    static List<EventInfo> CrashEvents()
    {
        lock (cache)
            if (crashEvents != null && DateTime.UtcNow - crashEventsAt < TimeSpan.FromMinutes(5)) return crashEvents;
        List<EventInfo> list;
        try { list = EventLogReader.Crashes(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { list = []; }
        lock (cache) { crashEvents = list; crashEventsAt = DateTime.UtcNow; }
        return list;
    }

    // ---- for the tray flyout (§5.10) ----

    /// <summary>
    /// Score, reason and one tip of TODAY for the tray flyout. Uses today's report from memory when it is under 10 minutes old (filled by the Checkup
    /// page or an earlier call), else loads today once in the background. Call on the UI thread; await it, then render.
    /// </summary>
    public static async Task<CheckupGist> GistAsync()
    {
        if (Cached(Period.Today) is { } c && DateTime.Now - c.Made < TimeSpan.FromMinutes(10)) return CheckupGist.From(c.Report, c.Tips);
        var b = await LoadAsync(Period.Today);
        return CheckupGist.From(b.Report, b.Tips);
    }

    /// <summary>The gist from memory only (any age), null before today's report exists. Cheap: for drawing at once, then call <see cref="GistAsync"/>.</summary>
    public static CheckupGist? GistNow() => Cached(Period.Today) is { } c ? CheckupGist.From(c.Report, c.Tips) : null;

    // ---- shared helpers ----

    /// <summary>The main-log battery samples of the last two days (time, %), oldest first. Background thread.</summary>
    public static List<(DateTime T, double Pct)> BatterySamples(DateTime now, string? dir = null)
    {
        var list = new List<(DateTime, double)>();
        foreach (var d in new[] { now.Date.AddDays(-1), now.Date })
        {
            var file = CsvLog.FileFor(d, "meterkast", dir);
            if (!File.Exists(file)) continue;
            try { list.AddRange(CsvLog.ReadSamples(DayLoader.ReadShared(file)).Where(s => double.IsFinite(s.Battery)).Select(s => (s.Time, s.Battery))); }
            catch (IOException) { }
        }
        return list;
    }
}
