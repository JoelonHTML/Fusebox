using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fusebox.App.Pages;

/// <summary>
/// Shared by ProcessesPage and AppPage: samples processes on a worker thread when a visible page asks (<see cref="Refresh"/>),
/// keeps the in-memory history, and caches icons. Idle (no cost) while no page calls it.
/// </summary>
public static class ProcessMonitor
{
    static readonly ProcessSampler Sampler = new();
    static readonly Dictionary<string, ImageSource?> Icons = [];
    static readonly Dictionary<string, IconPixels?> Pixels = [];   // filled on the worker thread (lock)
    static long lastRun;
    static bool busy;

    public static AppHistory History { get; } = new();
    public static ProcessSnapshot? Last { get; private set; }

    /// <summary>Raised on the UI thread after each new snapshot.</summary>
    public static event Action? Changed;

    /// <summary>Call from App.Live.Updated on the UI thread. Skips while the previous run is busy; low impact = every 10 s.</summary>
    public static async void Refresh(bool force = false)
    {
        if (busy) return;
        long now = Stopwatch.GetTimestamp();
        if (!force && Last != null && Prefs.Get("lowImpact", false) && Stopwatch.GetElapsedTime(lastRun, now).TotalSeconds < 10) return;
        busy = true;
        try
        {
            var snap = await Task.Run(() =>
            {
                var s = Sampler.Sample();
                int budget = 40;   // shell icon lookups per tick, so the first tick isn't slow
                foreach (var g in s.Groups)
                {
                    if (g.ExePath is not { } p) continue;
                    lock (Pixels) { if (Pixels.ContainsKey(p) || budget-- <= 0) continue; }
                    var px = IconReader.Read(p);
                    lock (Pixels) Pixels[p] = px;
                }
                return s;
            });
            lastRun = Stopwatch.GetTimestamp();
            History.Record(snap.Groups, snap.Time);
            Last = snap;
            Changed?.Invoke();
        }
        catch { /* a failed sample just skips this tick */ }
        finally { busy = false; }
    }

    public static AppGroup? Find(string key) => Last?.Groups.FirstOrDefault(g => g.Key == key);

    /// <summary>Icon for an exe: pixels are read once per path on the worker, the bitmap is built here (UI thread). Null = none.</summary>
    public static ImageSource? IconFor(string? path)
    {
        if (path == null) return null;
        if (Icons.TryGetValue(path, out var cached)) return cached;
        IconPixels? px;
        lock (Pixels) { if (!Pixels.TryGetValue(path, out px)) return null; }   // not read yet: try again next tick
        ImageSource? src = null;
        try
        {
            if (px != null)
            {
                var bmp = new WriteableBitmap(px.Width, px.Height);
                using (var s = bmp.PixelBuffer.AsStream()) s.Write(px.Bgra, 0, px.Bgra.Length);
                bmp.Invalidate();
                src = bmp;
            }
        }
        catch { }
        return Icons[path] = src;
    }
}
