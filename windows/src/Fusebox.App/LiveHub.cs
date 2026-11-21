using Fusebox.Core;
using Fusebox.Platform;
using Microsoft.UI.Dispatching;

namespace Fusebox.App;

/// <summary>
/// The one clock. Measures every 2 s (5 s in low impact) on a worker thread, keeps the last hour,
/// and only wakes the UI (<see cref="Updated"/>) when someone is looking — like the Mac's Meter.
/// </summary>
public sealed partial class LiveHub
{
    const int HourAt2s = 1800;
    readonly LiveSampler sampler = new();
    DispatcherQueueTimer? timer;
    bool busy;

    public History Cpu { get; } = new(HourAt2s);
    public History Ram { get; } = new(HourAt2s);
    public History NetIn { get; } = new(HourAt2s);
    public History NetOut { get; } = new(HourAt2s);
    public LiveSample? Last { get; private set; }

    /// <summary>Raised on the UI thread after a sample, only while <see cref="Watched"/>.</summary>
    public event Action? Updated;

    /// <summary>Set by the main window and the tray panel: true when one of them is on screen.</summary>
    public Func<bool> Watched { get; set; } = () => true;

    public static TimeSpan Interval => TimeSpan.FromSeconds(Prefs.Get("lowImpact", false) ? 5 : 2);

    public void Start()
    {
        if (timer != null) return;
        timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Interval;
        timer.IsRepeating = true;
        timer.Tick += async (_, _) => await Tick();
        timer.Start();
        _ = Tick();
    }

    public void ApplyInterval() { if (timer != null) timer.Interval = Interval; }

    async Task Tick()
    {
        if (busy) return;
        busy = true;
        try
        {
            var s = await Task.Run(sampler.Sample);
            Last = s;
            Cpu.Add(s.Cpu); Ram.Add(s.RamUsed); NetIn.Add(s.NetIn); NetOut.Add(s.NetOut);
            if (Watched()) Updated?.Invoke();
        }
        finally { busy = false; }
    }
}
