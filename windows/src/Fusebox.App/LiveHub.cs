using System.Text;
using Fusebox.Core;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Microsoft.UI.Dispatching;

namespace Fusebox.App;

/// <summary>
/// The one clock (Mac Meter). Measures every 2 s (5 s in Low impact) on a worker thread, always keeps history, logs CSVs and watches
/// peaks, and only wakes the pages (<see cref="Updated"/>) while someone is looking. Once a minute it measures every app
/// (<see cref="Apps"/>, apps-…csv) and raises <see cref="MinuteTick"/> for per-minute work (alerts, peak notifications, self log).
/// Everything public is read and raised on the UI thread.
/// </summary>
public sealed partial class LiveHub
{
    readonly LiveSampler sampler = new();
    readonly ProcessSampler appSampler = new();   // its own instance: CPU per app = average over the minute since the last call
    readonly BatteryEta eta = new();
    readonly StringBuilder samples = new(), peakLines = new(), appLines = new();
    DispatcherQueueTimer? timer;
    bool busy, appsBusy;
    int ticks;
    DateTime lastMinute = DateTime.MinValue, lastFlush = DateTime.Now, logDay;

    /// <summary>Every sample of the last hour + today's minute averages. Use <see cref="Values"/> for graphs.</summary>
    public SampleHistory History { get; } = new();
    public PeakWatch Peaks { get; } = new();
    public LiveSample? Last { get; private set; }
    public HardwareInfo Hardware => LiveSampler.Hardware;

    /// <summary>The last minute's app measurement (all groups, CPU averaged over the minute). Null before the first minute.</summary>
    public IReadOnlyList<AppGroup>? Apps { get; private set; }

    /// <summary>Raised after a sample, only while <see cref="Watched"/>. Pages subscribe in OnNavigatedTo, unsubscribe in OnNavigatedFrom.</summary>
    public event Action? Updated;

    /// <summary>Raised after every sample, watched or not. Only for work that is trivially cheap (the tray icon tooltip/bitmap).</summary>
    public event Action? Sampled;

    /// <summary>Raised once a minute (≥ 59 s apart) after the app measurement, watched or not: alerts, peak notifications, self log.</summary>
    public event Action? MinuteTick;

    /// <summary>Set by App: true when the main window or the tray flyout is on screen (or during the first 5 ticks).</summary>
    public Func<bool> Watched { get; set; } = () => true;

    public static int StepSeconds => LowImpact.On ? 5 : 2;
    public static TimeSpan Interval => TimeSpan.FromSeconds(StepSeconds);

    /// <summary>pref shortMinutes clamped 1–60.</summary>
    public static int ShortMinutes => TimeRanges.ShortMinutes(Prefs.Long("shortMinutes"));

    /// <summary>One metric's values for a period, oldest first (graphs, detail stats).</summary>
    public List<double> Values(Metric m, TimeRange r) => Metrics.Values(History.Range(r, ShortMinutes, StepSeconds), m);

    /// <summary>Average CPU over the last 30 s (status sentence, alerts).</summary>
    public double Cpu30 => Metrics.Stats(Metrics.Values(History.Since(30, StepSeconds), Metric.Cpu)).Avg;

    /// <summary>The status sentence (§8.5) for the current values.</summary>
    public (string Text, int Level) Status => Last is { } s
        ? StatusSentence.Of(s.Pressure, s.TempC, Cpu30, Prefs.Double("warnCPU"), Prefs.Double("warnTemp")) : (Strings.L("All calm"), 0);

    public LiveHub()
    {
        Peaks.Closed += p =>
        {
            Blame(p);
            peakLines.Append(PeakWatch.CsvLine(p));
        };
    }

    public void Start()
    {
        if (timer != null) return;
        _ = StartAsync();
    }

    async Task StartAsync()
    {
        // Today's earlier minutes from the CSV (so "Today" isn't empty after a restart), header migration and retention: off the UI thread.
        var now = DateTime.Now;
        var minutes = await Task.Run(() =>
        {
            CsvLog.MigrateHeader(CsvLog.FileFor(now), CsvLog.Header);
            CsvLog.MigrateHeader(CsvLog.FileFor(now, "apps"), AppsLog.Header);
            CsvLog.MigrateHeader(CsvLog.FileFor(now, "peaks"), PeakWatch.CsvHeader, PeakWatch.V1Columns);
            CsvLog.Prune(Prefs.Int("keepDays"), now);
            try { return SampleHistory.MinuteAverages(CsvLog.ReadSamples(File.ReadAllText(CsvLog.FileFor(now)))); }
            catch { return []; }
        });
        History.Seed(minutes, DateTime.Now);
        try { Peaks.Seed(PeakWatch.Parse(await File.ReadAllTextAsync(CsvLog.FileFor(now, "peaks"))), DateTime.Now); } catch { }
        logDay = DateTime.Now.Date;
        timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Interval;
        timer.IsRepeating = true;
        timer.Tick += async (_, _) => await Tick();
        timer.Start();
        _ = Task.Run(appSampler.Sample);   // zero point for the first minute's CPU per app
        await Tick();
    }

    /// <summary>Low impact changed: new interval (the timer keeps one tick per interval, no extra wake-ups).</summary>
    public void ApplyInterval() { if (timer != null) timer.Interval = Interval; }

    /// <summary>The window or flyout just came back: show the newest values now instead of at the next tick.</summary>
    public void Refresh() { if (Last != null && Watched()) Updated?.Invoke(); }

    async Task Tick()
    {
        if (busy) return;
        busy = true;
        try
        {
            var s = await Task.Run(sampler.Sample);
            s.Pressure = MemoryPressure.FromRamPercent(s.RamPercent);
            eta.Add(s.Time, s.Battery);
            if (s.Charging && double.IsNaN(s.BatteryMinutes)) s.BatteryMinutes = eta.ToFull(s.Time, s.Battery);
            Last = s;
            History.Add(s);
            if (s.Time.Date != logDay) Flush(s.Time);   // midnight: the old day's lines go to the old day's file
            Peaks.Feed(s, StepSeconds, Prefs.Double("warnTemp"));
            samples.Append(CsvLog.Line(s));
            ticks++;
            if ((s.Time - lastMinute).TotalSeconds >= 59) Minute(s);
            if ((s.Time - lastFlush).TotalSeconds >= 60) Flush(s.Time);
            Sampled?.Invoke();
            if (ticks <= 5 || Watched()) Updated?.Invoke();
        }
        catch { /* a failed sample skips this tick */ }
        finally { busy = false; }
    }

    /// <summary>Per-minute work: battery health (once a day), the app measurement + apps log, then <see cref="MinuteTick"/>.</summary>
    void Minute(LiveSample s)
    {
        lastMinute = s.Time;
        if (double.IsFinite(s.BatteryHealth)) _ = Task.Run(() => CsvLog.AppendBatteryHealth(s.Time, s.BatteryHealth, s.BatteryCycles));
        if (appsBusy || ticks < 3) { MinuteTick?.Invoke(); return; }
        appsBusy = true;
        _ = MeasureApps(s.Time);
    }

    async Task MeasureApps(DateTime t)
    {
        try
        {
            var snap = await Task.Run(appSampler.Sample);
            Apps = snap.Groups;
            appLines.Append(AppsLog.Lines(t, snap.Groups));
        }
        catch { }
        finally { appsBusy = false; }
        MinuteTick?.Invoke();
    }

    /// <summary>Peak cause from the last minute's apps (Live agent: refine with a snapshot at the peak's start, §3.9).</summary>
    void Blame(Peak p)
    {
        // Cause snapshotted at the start of the peak (Live), else the last minute's top app.
        if (PeakSnapshots.Cause(p) is { } c) { p.App = c.Name; p.AppValue = c.Value; return; }
        if (Apps == null || p.Kind == PeakKind.Io) return;
        Func<AppGroup, double> by = p.Kind switch { PeakKind.Gpu => g => g.Gpu, PeakKind.Mem => g => g.Ram / 1073741824.0, _ => g => g.Cpu };
        var top = Apps.Where(g => g.Kind != AppKind.Windows).MaxBy(by);
        if (top != null && by(top) > 0) { p.App = top.Name; p.AppValue = by(top); }
    }

    void Flush(DateTime now, bool wait = false)
    {
        lastFlush = now;
        var day = logDay;
        logDay = now.Date;
        if (samples.Length == 0 && peakLines.Length == 0 && appLines.Length == 0) return;
        string a = samples.ToString(), b = peakLines.ToString(), c = appLines.ToString();
        samples.Clear(); peakLines.Clear(); appLines.Clear();
        void Write()
        {
            if (b.Length > 0) CsvLog.Append(CsvLog.FileFor(day, "peaks"), PeakWatch.CsvHeader, b);
            if (c.Length > 0) CsvLog.Append(CsvLog.FileFor(day, "apps"), AppsLog.Header, c);
            // Disk full: keep the main log in memory (above 2 MB only the last 1 MB) and try again at the next flush.
            if (a.Length > 0 && !CsvLog.Append(CsvLog.FileFor(day), CsvLog.Header, a) && !wait)
                ui?.TryEnqueue(() => { samples.Insert(0, a); CsvLog.Cap(samples); });
        }
        if (wait) Write(); else _ = Task.Run(Write);
    }

    readonly DispatcherQueue? ui = DispatcherQueue.GetForCurrentThread();

    /// <summary>On quit: write what is still buffered.</summary>
    public void Shutdown()
    {
        timer?.Stop();
        Flush(Last?.Time ?? DateTime.Now, wait: true);
    }
}
