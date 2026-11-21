using static Fusebox.Core.Strings;

namespace Fusebox.Core.Processes;

/// <summary>Wording shared by the list's detail panel and the app page (Mac keys reused so translations apply).</summary>
public static class ProcessText
{
    /// <summary>One decimal below 10 %, none above ("0.4%", "37%").</summary>
    public static string Pct(double p) => (p < 10 ? p.ToString("0.0", Culture) : p.ToString("0", Culture)) + "%";

    public static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes < 1440 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes / 1440} d {minutes % 1440 / 60} h";

    public enum Verdict { Normal, HeavyCpu, HeavyBackground, HighMemory }

    public static Verdict Judge(AppGroup g, double ramTotal)
    {
        if (g.Cpu >= 50) return Verdict.HeavyCpu;
        if (g.Kind != AppKind.App && g.Cpu >= 10) return Verdict.HeavyBackground;
        if (g.Ram >= 2e9 || (ramTotal > 0 && g.Ram / ramTotal >= 0.15)) return Verdict.HighMemory;
        return Verdict.Normal;
    }

    public static string VerdictText(Verdict v) => v switch
    {
        Verdict.HeavyCpu => L("Heavy: uses a lot of processor"),
        Verdict.HeavyBackground => L("Heavy for a background task"),
        Verdict.HighMemory => L("Uses a lot of memory"),
        _ => L("Normal"),
    };

    /// <summary>The plain-language sentences about a group, in display order (empty ones left out).</summary>
    public static List<string> Lines(AppGroup g, int cpus, double ramTotal, DateTime now)
    {
        var l = new List<string>();
        if (g.Explanation.Length > 0) l.Add(L("What is this? %@.", g.Explanation));
        l.Add(L("%@ is using %@ of your processor right now (≈ %@ of the %lld cores) and %@ of memory (%@ of your %lld GB).",
            g.Name, Pct(g.Cpu), (g.Cpu / 100 * cpus).ToString("0.0", Culture), cpus, Fmt.Bytes(g.Ram),
            Pct(ramTotal > 0 ? g.Ram / ramTotal * 100 : 0), (long)Math.Round(ramTotal / 1e9)));
        if (g.Gpu >= 0.5) l.Add(L("Uses the graphics chip %@ of the time.", Pct(Math.Min(g.Gpu, 100))));
        if (g.Threads > 0) l.Add(L("Runs %lld threads (tasks at the same time).", g.Threads));
        if (g.StartedLocal is { } s) l.Add(L("Running for %@.", Duration(Math.Max((int)(now - s).TotalMinutes, 1))));
        l.Add(g.Pids.Count > 1 ? L("It consists of %lld parts (processes).", g.Pids.Count) : L("It's a single process."));
        return l;
    }
}
