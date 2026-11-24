using static Fusebox.Core.Strings;

namespace Fusebox.Core.Alerts;

/// <summary>The app behind a busy PC (never "Windows" itself): name, CPU % of the whole PC, memory in bytes.</summary>
public sealed record AppUse(string Name, double Cpu, double RamBytes);

/// <summary>One kind's settings as stored (alert.&lt;id&gt;.on / limit / min).</summary>
public sealed record AlertSetting(string Id, bool On, double Limit, int Minutes);

/// <summary>Everything one evaluation needs. <see cref="Last"/> = the samples of the last minute.</summary>
public sealed record AlertInput(IReadOnlyList<LiveSample> Last, double StepSeconds, bool NotifyOn, bool PingOn, int CooldownMinutes,
    DateTime Now, AppUse? TopCpu, AppUse? TopRam);

/// <summary>A notification the engine wants shown.</summary>
public sealed record AlertFire(string Id, string Title, string Body);

/// <summary>The seven alert kinds (Mac Alert enum, §9.12 / §11.2): titles, ranges, descriptions and the per-minute condition text.</summary>
public static class AlertKinds
{
    public static readonly string[] Ids = ["cpu", "heat", "mem", "disk", "net", "battery", "app"];

    public static string Key(string id, string field) => $"alert.{id}.{field}";

    public static string Title(string id) => id switch
    {
        "cpu" => L("Processor busy"), "heat" => L("PC too hot"), "mem" => L("Memory full"), "disk" => L("Storage almost full"),
        "net" => L("Internet slow or down"), "battery" => L("Battery low"), _ => L("One app hogging"),
    };

    /// <summary>Limit range and step of the stepper.</summary>
    public static (double Min, double Max, double Step) Range(string id) => id switch
    {
        "cpu" or "app" => (20, 100, 5), "heat" => (70, 110, 5), "mem" => (2, 4, 2), "disk" => (1, 200, 1), "net" => (50, 1000, 50), _ => (5, 50, 5),
    };

    /// <summary>The limit in words: "above 90%", "less than 10 GB free".</summary>
    public static string Describe(string id, double limit)
    {
        long v = (long)limit;
        return id switch
        {
            "cpu" or "app" => L("above %lld%%", v), "heat" => L("above %lld °C", v),
            "mem" => limit >= 4 ? L("pressure critical") : L("pressure high"),
            "disk" => L("less than %lld GB free", v), "net" => L("above %lld ms or offline", v), _ => L("%lld%% or less", v),
        };
    }

    static double Avg(IEnumerable<double> v)
    {
        double s = 0; int n = 0;
        foreach (var x in v) if (double.IsFinite(x)) { s += x; n++; }
        return n == 0 ? double.NaN : s / n;
    }

    /// <summary>Notification text when the last minute is over the limit, otherwise null (Mac Alert.check).</summary>
    public static string? Check(string id, IReadOnlyList<LiveSample> last, AppUse? topCpu, AppUse? topRam, double limit, int minutes, bool pingOn)
    {
        if (last.Count == 0) return null;
        var s = last[^1];
        switch (id)
        {
            case "cpu":
                {
                    double cpu = Avg(last.Select(x => x.Cpu));
                    if (!(cpu >= limit)) return null;
                    return L("Your PC has been at %lld%% processor for %lld min", (long)cpu, minutes)
                        + (topCpu is { } t ? L(" — mostly because of %@", t.Name) : "");
                }
            case "heat":
                {
                    double t = Avg(last.Select(x => x.TempC));
                    return t >= limit ? L("Your PC has been at %lld °C for %lld min and is slowing itself down", (long)t, minutes) : null;
                }
            case "mem":
                if (!(s.Pressure >= limit)) return null;
                return L("Your memory is full") + (topRam is { } m ? L(" — %@ is using %@ GB", m.Name, Fmt.One(m.RamBytes / 1e9)) : "");
            case "disk":
                {
                    double free = s.DiskFree / 1e9;
                    return free > 0 && free < limit ? L("Only %lld GB free on your SSD", (long)free) : null;
                }
            case "net":
                {
                    if (!pingOn) return null;
                    var pings = last.Select(x => x.PingMs).Where(double.IsFinite).ToList();
                    if (pings.Count == 0) return L("No internet for %lld min", minutes);
                    double p = pings.Average();
                    return p >= limit ? L("Internet has been slow for %lld min (%lld ms)", minutes, (long)p) : null;
                }
            case "battery":
                return double.IsFinite(s.Battery) && !s.Charging && s.Battery <= limit ? L("Battery at %lld%% — plug in the charger", (long)s.Battery) : null;
            default:
                return topCpu is { } a && a.Cpu >= limit ? L("%@ has been using %lld%% processor for %lld min", a.Name, (long)a.Cpu, minutes) : null;
        }
    }

    /// <summary>The settings page's example text: a measurement just over the limit, "One app" with a made-up Safari.</summary>
    public static string Example(string id, double limit, int minutes)
    {
        var s = new LiveSample
        {
            Cpu = Math.Min(limit + 2, 100), TempC = limit + 2, Pressure = 4, DiskFree = limit * 0.7 * 1e9, PingMs = limit + 40,
            Battery = Math.Max(limit - 2, 1), Charging = false,
        };
        return Check(id, [s], null, null, limit, minutes, true)
            ?? L("%@ has been using %lld%% processor for %lld min", "Safari", (long)Math.Min(limit + 5, 100), minutes);
    }
}

/// <summary>
/// The per-minute evaluation (§11.2): needs ≥ 40 s of samples in the last minute (else every streak resets), counts consecutive minutes
/// over the limit per kind, and fires when the streak ≥ minutes in a row and the kind's last alert is at least the cooldown ago
/// (alertCooldown minutes; disk 24 h). Pure: the App runner feeds it once a minute and shows what comes out.
/// </summary>
public sealed class AlertEngine
{
    public const double DiskCooldownSeconds = 86_400;
    readonly Dictionary<string, int> streak = [];
    readonly Dictionary<string, DateTime> lastAlert = [];

    public int Streak(string id) => streak.GetValueOrDefault(id);

    public List<AlertFire> Evaluate(AlertInput i, IReadOnlyList<AlertSetting> settings)
    {
        var fires = new List<AlertFire>();
        if (!i.NotifyOn || i.Last.Count * i.StepSeconds < 40) { streak.Clear(); return fires; }
        foreach (var a in settings)
        {
            int need = Math.Max(1, a.Minutes);
            var text = a.On ? AlertKinds.Check(a.Id, i.Last, i.TopCpu, i.TopRam, a.Limit, need, i.PingOn) : null;
            if (text == null) { streak[a.Id] = 0; continue; }
            int n = streak[a.Id] = streak.GetValueOrDefault(a.Id) + 1;
            double cooldown = a.Id == "disk" ? DiskCooldownSeconds : Math.Max(1, i.CooldownMinutes) * 60.0;
            if (n < need || (i.Now - lastAlert.GetValueOrDefault(a.Id, DateTime.MinValue)).TotalSeconds < cooldown) continue;
            lastAlert[a.Id] = i.Now;
            fires.Add(new(a.Id, AlertKinds.Title(a.Id), text));
        }
        return fires;
    }
}
