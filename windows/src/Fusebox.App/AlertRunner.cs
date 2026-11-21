using Fusebox.Core;
using Fusebox.Core.Alerts;
using Fusebox.Core.Processes;

namespace Fusebox.App;

/// <summary>
/// Runs the alerts (§11.2) once a minute on <c>App.Live.MinuteTick</c>: the last minute of samples + the busiest apps go to the pure
/// <see cref="AlertEngine"/>, what it returns is shown as a notification whose click opens the tab that explains it.
/// Nothing runs between ticks.
/// </summary>
public static class AlertRunner
{
    static readonly AlertEngine engine = new();
    static bool started;

    public static void Start()
    {
        if (started) return;
        started = true;
        App.Live.MinuteTick += Tick;
    }

    /// <summary>The tab a notification of this kind opens.</summary>
    public static string TabFor(string kind) => kind switch { "net" => "Network", "cpu" or "mem" or "app" => "Processes", _ => "Checkup" };

    /// <summary>Limit and minutes as stored, for every kind.</summary>
    public static AlertSetting[] Settings() => AlertKinds.Ids.Select(id => new AlertSetting(id, Prefs.Bool(AlertKinds.Key(id, "on")),
        Prefs.Double(AlertKinds.Key(id, "limit")), (int)Math.Max(1, Prefs.Long(AlertKinds.Key(id, "min"))))).ToArray();

    static void Tick()
    {
        try
        {
            var apps = App.Live.Apps?.Where(a => a.Kind != AppKind.Windows).ToList() ?? [];
            AppUse? Use(AppGroup? g) => g == null ? null : new(g.Name, g.Cpu, g.Ram);
            var input = new AlertInput(App.Live.History.Since(60, LiveHub.StepSeconds), LiveHub.StepSeconds, Prefs.Bool("notify"),
                Prefs.Str("pingHost").Length > 0, (int)Prefs.Long("alertCooldown"), DateTime.Now,
                Use(apps.Where(a => a.Cpu > 0).MaxBy(a => a.Cpu)), Use(apps.Where(a => a.Ram > 0).MaxBy(a => a.Ram)));
            foreach (var f in engine.Evaluate(input, Settings()))
                Notify.Send(f.Title, f.Body, new("", "open", ("tab", TabFor(f.Id))));
        }
        catch { /* a failed evaluation skips this minute */ }
    }
}
