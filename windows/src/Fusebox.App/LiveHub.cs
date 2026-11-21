using System.Text;
using Fusebox.Core;
using Fusebox.Platform;
using Microsoft.UI.Dispatching;

namespace Fusebox.App;

/// <summary>
/// The one clock. Measures every 2 s (5 s in low impact) on a worker thread, always logs (CSV once a minute) and watches for peaks,
/// and only wakes the pages (<see cref="Updated"/>) when someone is looking — like the Mac's Meter.
/// </summary>
public sealed partial class LiveHub
{
    readonly LiveSampler sampler = new();
    readonly BatteryEta eta = new();
    readonly StringBuilder samples = new(), peakLines = new();
    DispatcherQueueTimer? timer;
    bool busy;
    int lastMinute = -1;

    public Dictionary<Metric, MetricSeries> Series { get; } = Enum.GetValues<Metric>().ToDictionary(m => m, _ => new MetricSeries());
    public PeakWatch Peaks { get; } = new();
    public LiveSample? Last { get; private set; }
    public HardwareInfo Hardware => LiveSampler.Hardware;

    /// <summary>Raised on the UI thread after a sample, only while <see cref="Watched"/>.</summary>
    public event Action? Updated;

    /// <summary>Raised after every sample, watched or not. Only for work that is trivially cheap (the tray tooltip).</summary>
    public event Action? Sampled;

    /// <summary>Set by App: true when the main window or the tray panel is on screen.</summary>
    public Func<bool> Watched { get; set; } = () => true;

    public static int StepSeconds => LowImpact.On ? 5 : 2;
    public static TimeSpan Interval => TimeSpan.FromSeconds(StepSeconds);

    public LiveHub() => Peaks.Closed += p => peakLines.Append(PeakWatch.CsvLine(p));

    public void Start()
    {
        if (timer != null) return;
        _ = StartAsync();
    }

    async Task StartAsync()
    {
        // Today's earlier minutes from the CSV (so "Today" is not empty after a restart); measuring starts right after.
        var seeds = await Task.Run(() =>
        {
            var d = new Dictionary<Metric, List<double>>();
            try
            {
                var csv = File.ReadAllText(CsvLog.FileFor(DateTime.Now));
                List<double> Col(string c) => CsvLog.MinuteAverages(csv, c);
                List<double> Sum(string a, string b) { var x = Col(a); var y = Col(b); return x.Count == y.Count ? x.Zip(y, (p, q) => p + q).ToList() : x; }
                d[Metric.Cpu] = Col("cpu_pct"); d[Metric.Gpu] = Col("gpu_pct"); d[Metric.Ram] = Col("ram_pct");
                d[Metric.Net] = Sum("down_Bps", "up_Bps"); d[Metric.Ssd] = Sum("lezen_Bps", "schrijven_Bps"); d[Metric.Bat] = Col("batterij_pct");
            }
            catch { }
            CsvLog.Prune(Prefs.Int("keepDays"));
            return d;
        });
        foreach (var (m, v) in seeds) Series[m].Seed(DateTime.Now, v);
        timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Interval;
        timer.IsRepeating = true;
        timer.Tick += async (_, _) => await Tick();
        timer.Start();
        await Tick();
    }

    public void ApplyInterval() { if (timer != null) timer.Interval = Interval; }

    /// <summary>The window or panel just came back: show the newest values now instead of at the next tick.</summary>
    public void Refresh() { if (Last != null && Watched()) Updated?.Invoke(); }

    async Task Tick()
    {
        if (busy) return;
        busy = true;
        try
        {
            var s = await Task.Run(sampler.Sample);
            eta.Add(s.Time, s.Battery);
            if (s.Charging && double.IsNaN(s.BatteryMinutes)) s.BatteryMinutes = eta.ToFull(s.Time, s.Battery);
            Last = s;
            foreach (var (m, series) in Series) series.Add(s.Time, Metrics.Value(m, s));
            Peaks.Feed(s, StepSeconds);
            samples.Append(CsvLog.Line(s));
            if (s.Time.Minute != lastMinute) { if (lastMinute >= 0) Flush(s.Time); lastMinute = s.Time.Minute; }
            Sampled?.Invoke();
            if (Watched()) Updated?.Invoke();
        }
        finally { busy = false; }
    }

    void Flush(DateTime day, bool wait = false)
    {
        if (samples.Length == 0 && peakLines.Length == 0) return;
        string a = samples.ToString(), b = peakLines.ToString();
        samples.Clear(); peakLines.Clear();
        void Write()
        {
            if (a.Length > 0) CsvLog.Append(CsvLog.FileFor(day), CsvLog.Header, a);
            if (b.Length > 0) CsvLog.Append(CsvLog.FileFor(day, "peaks"), PeakWatch.CsvHeader, b);
        }
        if (wait) Write(); else _ = Task.Run(Write);
    }

    /// <summary>On quit: write what is still buffered.</summary>
    public void Shutdown()
    {
        timer?.Stop();
        Flush(Last?.Time ?? DateTime.Now, wait: true);
    }
}
