using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fusebox.App.Pages;

/// <summary>
/// Shared by ProcessesPage and AppPage: samples processes on a worker thread when a visible page asks (<see cref="Refresh"/>),
/// keeps the in-memory history, and caches icons. Idle (no cost) while no page calls it. The always-on minute monitor lives in
/// LiveHub and uses the same <see cref="ProcessSampler"/> (same grouping keys, names and "Windows itself" leftover).
/// </summary>
public static class ProcessMonitor
{
    static readonly ProcessSampler Sampler = new();
    static readonly Dictionary<string, ImageSource?> Icons = [];
    static readonly Dictionary<string, IconPixels?> Pixels = [];   // filled on worker threads (lock)
    static readonly HashSet<string> Pending = [];                  // icon reads in flight (lock on Pixels)
    static long lastRun;
    static bool busy;
    static DispatcherQueue? ui;
    static Task<List<InstalledApp>>? installed;

    public static AppHistory History { get; } = new();
    public static ProcessSnapshot? Last { get; private set; }

    /// <summary>Raised on the UI thread after each new snapshot (and when an icon arrived).</summary>
    public static event Action? Changed;

    /// <summary>Call from App.Live.Updated on the UI thread. Skips while the previous run is busy; Low impact = every 10 s.</summary>
    public static async void Refresh(bool force = false)
    {
        ui ??= DispatcherQueue.GetForCurrentThread();
        if (busy) return;
        long now = Stopwatch.GetTimestamp();
        if (!force && Last != null && LowImpact.On && Stopwatch.GetElapsedTime(lastRun, now).TotalSeconds < 10) return;
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

    /// <summary>A running group by key, else a running app with the same name (apps that run from another path), like the Mac.</summary>
    public static AppGroup? FindRunning(string key, string? name)
    {
        var g = Find(key);
        if (g != null || string.IsNullOrEmpty(name)) return g;
        return Last?.Groups.FirstOrDefault(x => x.Kind == AppKind.App && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Installed apps, scanned once in the background on the first call (first keystroke in the search).</summary>
    public static Task<List<InstalledApp>> InstalledAsync() => installed ??= Task.Run(InstalledApps.All);

    /// <summary>Icon for an exe: pixels are read once per path on a worker (started here when nobody read them yet), the bitmap is built on the UI thread.
    /// Null = none (yet): the page is told through <see cref="Changed"/> when it arrives.</summary>
    public static ImageSource? IconFor(string? path)
    {
        if (path == null) return null;
        ui ??= DispatcherQueue.GetForCurrentThread();
        if (Icons.TryGetValue(path, out var cached)) return cached;
        IconPixels? px;
        lock (Pixels)
        {
            if (!Pixels.TryGetValue(path, out px))
            {
                if (Pending.Add(path))
                    _ = Task.Run(() =>
                    {
                        var read = IconReader.Read(path);
                        lock (Pixels) { Pixels[path] = read; Pending.Remove(path); }
                        ui?.TryEnqueue(() => Changed?.Invoke());
                    });
                return null;
            }
        }
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

    /// <summary>Explorer with the file selected.</summary>
    public static void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
    }
}
