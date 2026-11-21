using System.Text;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Checkup;

/// <summary>Pure scoring: inputs in, categories with findings and tips out. Same five categories as the Mac.</summary>
public static class HealthScore
{
    public const int RestartDays = 14;
    const string Update = "ms-settings:windowsupdate", Startup = "ms-settings:startupapps", Storage = "ms-settings:storagesense",
        Battery = "ms-settings:batterysaver", Power = "ms-settings:powersleep";

    public static CheckupResult Evaluate(CheckupInputs i)
    {
        var cats = new List<Category> { Performance(i), BatteryPower(i), Stability(i), StartupStorage(i), History(i) }
            .Where(c => c.Findings.Count > 0).ToList();
        int score = cats.Count == 0 ? 100 : (int)Math.Round(cats.Average(c => c.Score));
        return new(score, cats);
    }

    static Category Make(string id, string name, List<Finding> f, List<Tip> t) =>
        new(id, name, Math.Clamp(100 - f.Sum(x => x.Penalty), 0, 100), f, t);

    static string Pct(double v) => $"{Math.Round(v)}%";

    static Category Performance(CheckupInputs i)
    {
        var f = new List<Finding>(); var t = new List<Tip>();
        if (i.CpuAvg is { } cpu)
            f.Add(cpu >= 85 ? new(Level.Bad, L("The CPU is busy: %@ on average", Pct(cpu)))
                : cpu >= 60 ? new(Level.Warning, L("The CPU is fairly busy: %@ on average", Pct(cpu)))
                : new(Level.Good, L("CPU: %@, not busy", Pct(cpu))));
        if (i.RamPct is { } ram)
        {
            f.Add(ram >= 90 ? new(Level.Bad, L("Memory is full (%@)", Pct(ram)))
                : ram >= 80 ? new(Level.Warning, L("Memory is filling up (%@)", Pct(ram)))
                : new(Level.Good, L("Memory: %@, normal", Pct(ram))));
            if (ram >= 80) t.Add(new(L("Memory was (almost) full — close apps you're not using, or restart your PC")));
        }
        if (i.CommitPct is >= 90) f.Add(new(Level.Warning, L("Windows is almost out of virtual memory (%@ committed)", Pct(i.CommitPct.Value))));
        if (i.PendingReboot == true)
        {
            f.Add(new(Level.Warning, L("Windows is waiting for a restart to finish updating")));
            t.Add(new(L("Restart your PC to finish installing updates."), Update, L("Open Windows Update")));
        }
        if (i.UptimeDays is { } d && d >= RestartDays)
        {
            f.Add(new(Level.Warning, L("Not restarted for %lld days", (long)d)));
            t.Add(new(L("Not restarted for %lld days. A restart clears memory and finishes updates.\nHow to restart: click Start, then the power button, then Restart.", (long)d)));
        }
        return Make("performance", L("Performance"), f, t);
    }

    static Category BatteryPower(CheckupInputs i)
    {
        var f = new List<Finding>(); var t = new List<Tip>();
        if (i.Battery is { Percent: { } p } b)
        {
            var text = L("Your battery holds %lld%% of its original charge", p);
            f.Add(new(p < 60 ? Level.Bad : p < 80 ? Level.Warning : Level.Good, text));
            if (p < 80) t.Add(new(text + L(". A battery service gives you longer battery life again."), Battery, L("Open Battery")));
            if (b.Cycles is { } c) f.Add(new(Level.Good, L("%lld charge cycles", c)));
        }
        if (i.Sleep is { } s)
        {
            int n = s.NightWakes;
            f.Add(n >= 3 ? new(Level.Warning, L("Woke up %lld times at night in the last 7 days", n))
                : new(Level.Good, n == 0 ? L("No wake-ups at night in the last 7 days") : L("Woke up %lld times at night in the last 7 days", n)));
            if (n >= 3) t.Add(new(L("Your PC keeps waking up at night. Check what keeps it awake, or change the sleep settings."), Power, L("Open Power & sleep")));
        }
        return Make("battery", L("Battery & power"), f, t);
    }

    static Category Stability(CheckupInputs i)
    {
        var f = new List<Finding>(); var t = new List<Tip>();
        if (i.StabilityIndex is { } si)
            f.Add(new(si < 5 ? Level.Bad : si < 8 ? Level.Warning : Level.Good, L("Windows stability index: %.1f out of 10", si)));
        if (i.Crashes is { } c)
        {
            if (c.BlueScreens > 0) f.Add(new(Level.Bad, L("Blue screen: %lld times in the last 30 days", c.BlueScreens)));
            if (c.UnexpectedShutdowns > 0) f.Add(new(Level.Warning, L("Unexpected shutdown: %lld times in the last 30 days", c.UnexpectedShutdowns)));
            int apps = c.AppCrashes + c.Hangs;
            if (apps > 0) f.Add(new(apps >= 10 ? Level.Warning : Level.Good, L("Apps crashed or hung %lld times in the last 30 days", apps)));
            foreach (var (app, n) in c.TopApps.Where(a => a.Count >= 3).Take(3))
                t.Add(new(L("%@ crashed %lld times in the last 30 days. Update or reinstall it.", app, n)));
            if (c.BlueScreens > 0) t.Add(new(L("Blue screens are usually caused by a driver or failing hardware. Install Windows updates and update your drivers."), Update, L("Open Windows Update")));
            if (c.Total == 0) f.Add(new(Level.Good, L("No crashes in the last 30 days")));
        }
        return Make("stability", L("Stability"), f, t);
    }

    static Category StartupStorage(CheckupInputs i)
    {
        var f = new List<Finding>(); var t = new List<Tip>();
        if (i.StartupEnabled is { } n)
        {
            f.Add(new(n > 20 ? Level.Bad : n > 10 ? Level.Warning : Level.Good, L("%lld apps start with Windows", n)));
            if (n > 10) t.Add(new(L("Check which apps open automatically at startup — you can turn off what you don't need there"), Startup, L("Open Startup apps")));
        }
        foreach (var d in i.Drives ?? [])
        {
            long gb = d.Free / 1_000_000_000;
            if (d.FreePct < 10) f.Add(new(Level.Bad, L("Drive %@ is almost full (%lld GB free)", d.Name, gb)));
            else if (d.FreePct < 20) f.Add(new(Level.Warning, L("Drive %@ is getting full (%lld GB free)", d.Name, gb)));
            else f.Add(new(Level.Good, L("Drive %@: %lld GB free", d.Name, gb)));
            if (d.FreePct < 20) t.Add(new(L("Your disk is almost full (%lld GB free) — clean up large files; Windows gets slow without free space", gb), Storage, L("Open Storage")));
        }
        foreach (var d in i.Disks ?? [])
            if (d.Health is not ("Healthy" or "")) f.Add(new(Level.Bad, L("%@ reports its health as %@", d.Name, d.Health)));
        foreach (var r in i.Ssd ?? [])
            if (r.Wear is { } w) f.Add(new(w >= 90 ? Level.Bad : w >= 70 ? Level.Warning : Level.Good, L("SSD wear: %lld%%", w)));
        return Make("storage", L("Startup & storage"), f, t);
    }

    static Category History(CheckupInputs i)
    {
        var f = new List<Finding>(); var t = new List<Tip>();
        if (i.Crashes is { } c)
            f.Add(new(c.Total >= 30 ? Level.Bad : c.Total >= 10 ? Level.Warning : Level.Good,
                c.Total == 0 ? L("Nothing logged in the last 30 days") : L("%lld problems logged in the last 30 days", c.Total)));
        if (i.CpuMax is >= 98) f.Add(new(Level.Good, L("The CPU peaked at %@ in the last hour", Pct(i.CpuMax.Value))));
        return Make("history", L("History"), f, t);
    }

    /// <summary>"Why is my PC slow": every current problem in Performance and Startup &amp; storage, in plain words.</summary>
    public static List<string> WhySlow(CheckupInputs i)
    {
        var why = new[] { Performance(i), StartupStorage(i) }.SelectMany(c => c.Findings).Where(f => f.Level != Level.Good).Select(f => f.Text).ToList();
        if (why.Count == 0)
            why.Add(L("Nothing wrong: CPU %@, memory normal.", i.CpuAvg is { } c ? Pct(c) : "–"));
        return why;
    }

    /// <summary>Plain-text summary for the clipboard.</summary>
    public static string Report(CheckupResult r, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Fusebox Checkup — {now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"{L("Health score")}: {r.Score}/100");
        foreach (var c in r.Categories)
        {
            sb.AppendLine().AppendLine($"{c.Name}: {c.Score}/100");
            foreach (var f in c.Findings) sb.AppendLine($"  {(f.Level switch { Level.Bad => "[X]", Level.Warning => "[!]", _ => "[OK]" })} {f.Text}");
            foreach (var t in c.Tips) sb.AppendLine($"  -> {t.Text.Replace('\n', ' ')}");
        }
        return sb.ToString();
    }
}
