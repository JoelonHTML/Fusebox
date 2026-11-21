using Fusebox.Core;
using Fusebox.Core.Live;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>
/// App snapshots at the start of a peak (§3.9): on the first sample above a limit (max once per 30 s per kind) two process measurements
/// 1 s apart, off the UI thread, kept for an hour. The cause of a peak = the biggest app of the snapshot within 20 s of its top moment;
/// LiveHub.Blame (per-minute apps) is the fallback.
/// </summary>
public static class PeakSnapshots
{
    public sealed record Snap(DateTime Time, PeakKind Kind, IReadOnlyList<AppGroup> Groups);

    static readonly ProcessSampler sampler = new();
    static readonly List<Snap> snaps = [];
    static readonly Dictionary<PeakKind, DateTime> last = [];
    static bool started, busy;

    public static void Start()
    {
        if (started) return;
        started = true;
        App.Live.Peaks.Started += OnStarted;
    }

    static async void OnStarted(PeakKind kind)
    {
        if (busy || LowImpact.On && kind != PeakKind.Cpu) return;
        if (last.TryGetValue(kind, out var t) && (DateTime.Now - t).TotalSeconds < 30) return;
        last[kind] = DateTime.Now;
        busy = true;
        try
        {
            var groups = await Task.Run(() => { sampler.Sample(); Thread.Sleep(1000); return sampler.Sample().Groups; });
            snaps.Add(new(DateTime.Now, kind, groups));
            snaps.RemoveAll(s => (DateTime.Now - s.Time).TotalHours > 1);
        }
        catch { }
        finally { busy = false; }
    }

    /// <summary>The snapshot closest to the peak's top moment (≤ 20 s away), or null.</summary>
    public static Snap? Near(Peak p) =>
        snaps.Where(s => Math.Abs((s.Time - p.At).TotalSeconds) <= 20 || (s.Time >= p.Start && s.Time <= p.End.AddSeconds(20))).MinBy(s => Math.Abs((s.Time - p.At).TotalSeconds));

    /// <summary>Cause from the snapshot (name, value in the peak's unit), or null.</summary>
    public static (string Name, double Value)? Cause(Peak p) => Near(p) is { } s ? PeakInfo.Cause(p.Kind, s.Groups) : null;
}

/// <summary>
/// Sustained-peak notification (§3.10, §11.2): once a minute, for every running peak whose cause app is still running and that lasts at
/// least <c>alert.peak.min</c> minutes (−10 s): "‹App› is keeping your CPU busy", body "CPU 92% for 3 minutes", buttons "Stop app" (only when
/// the app can be stopped; graceful close, never a kill) and "Show" (Processes tab, that app's page). Cooldown per app: <c>alertCooldown</c> minutes.
/// <see cref="EnsureStarted"/> is idempotent (called by the Live page at launch).
/// </summary>
public static class PeakAlert
{
    static readonly Dictionary<string, DateTime> last = [];
    static bool started;

    public static void EnsureStarted()
    {
        if (started) return;
        started = true;
        PeakSnapshots.Start();
        App.Live.MinuteTick += Check;
        try { AppNotificationManager.Default.NotificationInvoked += (_, e) => Handle(e.Arguments); } catch { }
    }

    static void Check()
    {
        if (!Prefs.Bool("notify") || !Prefs.Bool("alert.peak.on") || App.Live.Apps is not { } apps) return;
        var running = new List<(Peak, string?)>();
        foreach (var kind in Enum.GetValues<PeakKind>())
        {
            if (App.Live.Peaks.Running(kind) is not { } p) continue;
            var cause = PeakSnapshots.Cause(p) ?? PeakInfo.Cause(kind, apps);
            running.Add((p, cause?.Name));
        }
        if (running.Count == 0) return;
        foreach (var n in PeakAlerts.Due(running, Prefs.Int("alert.peak.min"), Prefs.Int("alertCooldown"), last, name => apps.Any(g => g.Name == name), DateTime.Now))
            Show(n, apps.FirstOrDefault(g => g.Name == n.App));
    }

    static void Show(PeakNotice n, AppGroup? app)
    {
        try
        {
            var b = new AppNotificationBuilder().AddText(n.Title).AddText(n.Body);
            if (app is { CanStop: true, Kind: not AppKind.Windows })
                b.AddButton(new AppNotificationButton(L("Stop app")).AddArgument("action", "stop").AddArgument("app", app.Key));
            b.AddButton(new AppNotificationButton(L("Show")).AddArgument("action", "show").AddArgument("app", app?.Key ?? ""));
            if (app != null) b.AddArgument("action", "show").AddArgument("app", app.Key);   // clicking the notification itself = Show
            AppNotificationManager.Default.Show(b.BuildNotification());
        }
        catch { }
    }

    /// <summary>Notification activation: action=stop|show&amp;app=‹key›. Runs on a worker thread; hops to the UI.</summary>
    static void Handle(IDictionary<string, string> args)
    {
        if (!args.TryGetValue("action", out var action) || !args.TryGetValue("app", out var key) || action is not ("stop" or "show")) return;
        App.Main?.DispatcherQueue.TryEnqueue(() =>
        {
            if (action == "show") { App.ShowMain("Processes", key); return; }
            var pids = App.Live.Apps?.FirstOrDefault(g => g.Key == key)?.Pids.ToList();
            if (pids == null) return;
            _ = Task.Run(() => ProcessControl.RequestClose(pids));   // like closing its windows; nothing is killed
        });
    }
}
