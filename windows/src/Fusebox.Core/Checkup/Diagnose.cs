using static Fusebox.Core.Strings;

namespace Fusebox.Core.Checkup;

public sealed record DiagApp(string Name, bool Windows, double Cpu, double MemGb);

/// <summary>The live state "Check now" looks at (§5.6.2).</summary>
public sealed class DiagInput
{
    public double Cpu;                                   // mean of the last 30 samples
    public int Pressure = 1;
    public double TempC = double.NaN, DiskFreeGb = double.NaN, DiskTotalGb = double.NaN, PingMs = double.NaN;
    public double? UptimeDays;
    public int RestartDays = 14;
    public IReadOnlyList<DiagApp> Apps = [];             // last minute's measurement, biggest first not required
    public IReadOnlyList<string> JobsNow = [];
    public Report? Report;
    public DateTime Now = DateTime.Now;
}

public sealed record DiagResult(string Text, string? Culprit, bool NoAppData);

public static class Diagnose
{
    /// <summary>At most two sentences, in the order of the §5.6.2 table. The culprit is the app the Stop button would quit.</summary>
    public static DiagResult Run(DiagInput i)
    {
        var s = new List<string>();
        string? culprit = null;
        bool jobUsed = false;
        var own = i.Apps.Where(a => !a.Windows).OrderByDescending(a => a.Cpu).ToList();
        string? job = i.JobsNow.Count > 0 ? string.Join(". ", i.JobsNow.Select(Housekeeping.Tr)) : null;
        if (i.Cpu >= Counts.BusyCpu)
        {
            if (own.Count > 0 && own[0].Cpu >= 10) { s.Add(L("The CPU is at %@, mostly because of %@ (%@).", Fmt.Pct(i.Cpu), own[0].Name, Fmt.Pct(own[0].Cpu))); culprit = own[0].Name; }
            else if (job != null) { s.Add(L("The CPU is at %@, mostly because of Windows itself: %@", Fmt.Pct(i.Cpu), L("%@ — this passes by itself, no action needed.", job))); jobUsed = true; }
            else s.Add(L("The CPU is at %@, mostly because of Windows itself.", Fmt.Pct(i.Cpu)));
        }
        if (i.Pressure >= 2)
        {
            var top = own.OrderByDescending(a => a.MemGb).FirstOrDefault();
            bool name = top != null && top.MemGb >= 1 && culprit == null;
            s.Add((i.Pressure >= 4 ? L("Memory is full") : L("Memory is filling up")) + (name ? L(" — %@ is using %@ GB", top!.Name, Fmt.One(top.MemGb)) : "") + ".");
            if (name) culprit = top!.Name;
        }
        if (i.Report is { } r)
        {
            var run = r.Runaways.FirstOrDefault(x => (i.Now - x.End).TotalMinutes < 5 && x.App != culprit);
            if (run != null) { s.Add(L("%@ has been using a full processor core for %@.", run.App, CheckupFmt.Mins(run.Minutes))); culprit ??= run.App; }
            var leak = r.Leaks.FirstOrDefault(x => (i.Now - x.End).TotalMinutes < 30 && i.Apps.Any(a => a.Name == x.App));
            if (leak != null) { s.Add(L("%@ grew from %@ to %@ GB in %lld h — quitting and reopening it frees that memory.", leak.App, Fmt.One(leak.From), Fmt.One(leak.To), leak.Hours)); culprit ??= leak.App; }
        }
        if (i.TempC >= Counts.HotC) s.Add(L("Your PC is hot (%lld °C) and may be slowing itself down.", (long)Math.Round(i.TempC)));
        if (i.DiskTotalGb > 0 && i.DiskFreeGb / i.DiskTotalGb < 0.10) s.Add(L("Your disk is almost full (%lld GB free).", (long)i.DiskFreeGb));
        if (s.Count > 0 && job != null && !jobUsed) s.Add(L("%@ — this passes by itself, no action needed.", job));
        if (i.PingMs >= 100) s.Add(L("Internet is responding slowly (%lld ms) — that's probably not your PC.", (long)i.PingMs));
        if (s.Count > 0 && i.RestartDays > 0 && i.UptimeDays is { } up && up >= i.RestartDays)
            s.Add(L("Your PC hasn't been restarted for %lld days — a restart clears memory.", (long)up));
        if (s.Count == 0)
        {
            var t = double.IsFinite(i.TempC) ? $", {Math.Round(i.TempC)} °C" : "";
            var text = L("Nothing wrong: CPU %@, memory normal%@.", Fmt.Pct(i.Cpu), t);
            if (own.Count > 0 && own[0].Cpu > 0.5) text += L(" Biggest user: %@.", own[0].Name);
            if (job != null) text += " " + L("%@ — this passes by itself, no action needed.", job);
            return new(text, null, i.Apps.Count == 0);
        }
        return new(string.Join(" ", s.Take(2)), culprit, i.Apps.Count == 0);
    }
}
