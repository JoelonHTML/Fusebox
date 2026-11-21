using static Fusebox.Core.Strings;

namespace Fusebox.Core.Checkup;

/// <summary>Event Log → <see cref="CrashSummary"/> for a period (§5.6.7): 1000 crash, 1002 hang, 2004 out of memory, WER 1001 blue screen, Kernel-Power 41.</summary>
public static class CrashAnalysis
{
    public static CrashSummary Build(IEnumerable<EventInfo> events, DateTime from, DateTime to)
    {
        var crash = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        var hang = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int oom = 0, blue = 0, power = 0;
        foreach (var e in events.Where(e => e.Time >= from && e.Time < to))
            switch (e.Provider, e.Id)
            {
                case ("Application Error", 1000) when Name(e.At(0)) is { } a: (crash.TryGetValue(a, out var l) ? l : crash[a] = []).Add(e.Time); break;
                case ("Application Hang", 1002) when Name(e.At(0)) is { } h: hang[h] = hang.GetValueOrDefault(h) + 1; break;
                case ("Microsoft-Windows-Resource-Exhaustion-Detector", 2004): oom++; break;
                case ("Microsoft-Windows-WER-SystemErrorReporting", 1001): blue++; break;
                case ("Microsoft-Windows-Kernel-Power", 41): power++; break;
            }
        var apps = crash.Keys.Union(hang.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(a => new AppCrash(a, crash.TryGetValue(a, out var t) ? t.Count : 0, hang.GetValueOrDefault(a), crash.TryGetValue(a, out var t2) ? MaxInHour(t2) : 0))
            .OrderByDescending(a => a.Crashes + a.Hangs).ThenBy(a => a.App, StringComparer.OrdinalIgnoreCase).ToList();
        return new(apps, oom, blue, power);
    }

    /// <summary>"chrome.exe" → "chrome"; empty → null.</summary>
    static string? Name(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
    }

    /// <summary>Most crashes inside any sliding hour (crash loop: ≥ 5).</summary>
    public static int MaxInHour(List<DateTime> times)
    {
        var t = times.OrderBy(x => x).ToList();
        int best = 0, j = 0;
        for (int i = 0; i < t.Count; i++)
        {
            while (t[i] - t[j] > TimeSpan.FromHours(1)) j++;
            best = Math.Max(best, i - j + 1);
        }
        return best;
    }
}

/// <summary>Where a tip's button leads: stop a running app or open a Settings page / URI.</summary>
public sealed record TipAction(string Label, string? Uri = null, string? StopApp = null);

/// <summary>One tip (§5.6.1). <see cref="Group"/> follows the id prefix.</summary>
public sealed record CheckupTip(string Id, string Text, bool Serious, TipAction? Action = null)
{
    static readonly string[] SeriousPrefixes = ["loop", "jetsam", "disk", "mem", "hot", "battery", "leak", "restart", "bluescreen"];

    /// <summary>perf | power | stability | storage | history (the Mac's categories).</summary>
    public string Group => Id.StartsWith("battery") ? "power"
        : StartsWithAny(Id, "loop", "crashed", "stopped", "jetsam", "wifidrop", "wifiweak", "bluescreen") ? "stability"
        : StartsWithAny(Id, "disk", "login", "orphan") ? "storage" : "perf";

    public static bool IsSerious(string id) => StartsWithAny(id, SeriousPrefixes);
    static bool StartsWithAny(string id, params string[] p) => p.Any(x => id.StartsWith(x, StringComparison.Ordinal));

    /// <summary>Head at the first " — ", else at the first ". " (the head keeps the period); the rest starts with a capital.</summary>
    public (string Head, string Tail) Split()
    {
        var t = Text;
        int i = t.IndexOf(" — ", StringComparison.Ordinal);
        if (i >= 0) return (t[..i], CheckupFmt.Cap(t[(i + 3)..]));
        i = t.IndexOf(". ", StringComparison.Ordinal);
        return i >= 0 ? (t[..(i + 1)], CheckupFmt.Cap(t[(i + 2)..].Trim())) : (t, "");
    }
}

/// <summary>What the tip rules need besides the report.</summary>
public sealed class TipContext
{
    public double? UptimeDays;
    public int RestartDays = 14;
    public bool PendingReboot;
    public DateTime? LastUpdate;
    public BatteryHealth? Battery;
    public Func<string, bool> Stoppable = _ => false;
}

public static class Tips
{
    public const string Update = "ms-settings:windowsupdate", Startup = "ms-settings:startupapps", Storage = "ms-settings:storagesense", BatteryPage = "ms-settings:batterysaver";
    static readonly string[] Browsers = ["Google Chrome", "Chrome", "Microsoft Edge", "Edge", "Firefox", "Brave", "Brave Browser", "Opera", "Vivaldi", "Arc"];

    /// <summary>All tip rules in generation order (§5.6.1). The "login" tip is always last.</summary>
    public static List<CheckupTip> Build(Report r, TipContext c)
    {
        var tips = new List<CheckupTip>();
        void Add(string id, string text, TipAction? a = null) => tips.Add(new(id, text, CheckupTip.IsSerious(id), a));
        TipAction? Stop(string app) => c.Stoppable(app) ? new(L("Stop"), null, app) : null;
        string When = r.When;

        var leaking = new HashSet<string>();
        foreach (var l in r.Leaks.Take(2))
        {
            leaking.Add(l.App);
            Add("leak" + l.App, L("%@ keeps growing: +%@ GB per hour for %lld h (now %@ GB) — restart it to free memory.", l.App, Fmt.One(l.PerHour), l.Hours, Fmt.One(l.To)), Stop(l.App));
        }
        foreach (var x in r.Runaways.GroupBy(x => x.App).Select(g => g.First()).Take(2))
            Add("runaway" + x.App, L("%@ used a full processor core for %@ (%@).", x.App, CheckupFmt.Mins(x.Minutes), CheckupFmt.Span(x.Start, x.End)), Stop(x.App));

        // Heavy or long-running apps (not Windows itself, not already a leak).
        foreach (var a in r.All.Where(a => a.Name != "Windows" && !leaking.Contains(a.Name)
                     && (a.MemGb > 2 || (a.Minutes >= 30 && (a.MemGb > 0.3 || a.Cpu > 5)))).OrderByDescending(a => a.MemGb).Take(3))
        {
            if (a.MemGb > 2)
                Add("app" + a.Name, Browsers.Contains(a.Name, StringComparer.OrdinalIgnoreCase)
                    ? L("%@ uses %@ GB on average — close tabs you're not using; every tab costs memory", a.Name, Fmt.One(a.MemGb))
                    : L("%@ uses %@ GB on average — close windows and documents you're not using", a.Name, Fmt.One(a.MemGb)), Stop(a.Name));
            else if (Stop(a.Name) is { } stop)
                Add("app" + a.Name, L("%@ has been running for %@ (avg. %@%% CPU, %@ GB) — quit it if you're not using it", a.Name, CheckupFmt.Duration(a.Minutes), Fmt.One(a.Cpu), Fmt.One(a.MemGb)), stop);
        }

        foreach (var a in r.Crashes.Apps.Where(a => a.Loop).Take(2))
            Add("loop" + a.App, L("%@ keeps crashing and restarting (%lld× in one hour) — that costs battery. Update or remove it.", a.App, a.LoopCrashes));
        foreach (var a in r.Crashes.Apps.Where(a => a.Crashes >= 2 && !a.Loop).Take(2))
            Add("crashed" + a.App, L("%@ crashed %lld times %@. Update or reinstall it.", a.App, a.Crashes, When));
        foreach (var a in r.Crashes.Apps.Where(a => a.Hangs >= 2).Take(2))
            Add("stopped responding" + a.App, L("%@ stopped responding %lld times %@. Update or reinstall it.", a.App, a.Hangs, When));
        if (r.Crashes.OutOfMemory == 1)
            Add("jetsam", L("Windows warned once %@ that memory ran out. Quit apps you're not using, or restart your PC.", When));
        else if (r.Crashes.OutOfMemory > 1)
            Add("jetsam", L("Windows warned %lld times %@ that memory ran out. Quit apps you're not using, or restart your PC.", r.Crashes.OutOfMemory, When));
        if (r.Crashes.BlueScreens > 0)
            Add("bluescreen", L("Blue screens are usually caused by a driver or failing hardware. Install Windows updates and update your drivers."), new(L("Open Windows Update"), Update));

        if (c.Battery is { } b && (b.Worn))
            Add("battery", (b.Percent is { } p ? L("Your battery holds %lld%% of its original charge", p) : L("Your battery may need attention"))
                + L(". A battery service gives you longer battery life again."), new(L("Open Battery"), BatteryPage));

        // Windows: a pending restart is a serious tip with a way to the update page.
        if (c.PendingReboot)
            Add("restart", L("Restart needed — Windows is waiting for a restart to finish installing updates") + (c.LastUpdate is { } d ? " " + L("(last update %@).", d.ToString("d MMM yyyy", Culture)) : ""),
                new(L("Open Windows Update"), Update));
        if (c.UptimeDays is { } up && c.RestartDays > 0 && up >= c.RestartDays)
            Add("uptime", L("Not restarted for %lld days. A restart clears memory and finishes updates.\nHow to restart: click Start, then the power button, then Restart.", (long)up));

        if (r.DiskLow)
            Add("disk", L("Your disk is almost full (%lld GB free) — clean up large files; Windows gets slow without free space", (long)r.DiskFreeGb), new(L("Open Storage"), Storage));
        if (r.WifiDrops >= 3)
            Add("wifidrop", L("Wi-Fi dropped %lld× %@. Move closer to the router or restart it; a cable never drops.", r.WifiDrops, When));
        if (r.WeakWifiMin >= 15)
            Add("wifiweak", L("Weak Wi-Fi signal for %@ %@ — move closer to the router, or add a mesh point where you work", CheckupFmt.Duration(r.WeakWifiMin), When));
        if (r.PressureMin >= 30)
            Add("mem", L("Memory was (almost) full for %@ — close apps you're not using, or restart your PC", CheckupFmt.Duration(r.PressureMin)));
        if (r.HotMin >= 15)
            Add("hot", L("Your PC was hot (≥ 95 °C) for %@ — put it on a hard, flat surface and keep the vents clear", CheckupFmt.Duration(r.HotMin)));
        Add("login", L("Check which apps open automatically at startup — you can turn off what you don't need there"), new(L("Open Startup apps"), Startup));
        return tips;
    }
}
