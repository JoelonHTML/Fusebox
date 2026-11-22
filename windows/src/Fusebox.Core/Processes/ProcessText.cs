using System.Globalization;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Processes;

/// <summary>Wording shared by the list's detail panel and the app page (Mac keys reused so translations apply).</summary>
public static class ProcessText
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One decimal below 10 %, none above ("0.4%", "37%").</summary>
    public static string Pct(double p) => (p < 10 ? p.ToString("0.0", Culture) : p.ToString("0", Culture)) + "%";

    /// <summary>The Mac's mem(): "1.2 GB" from 1 GB (one decimal, always a dot), else "340 MB" (GB = 1024³).</summary>
    public static string Mem(double bytes)
    {
        double gb = bytes / 1073741824.0;
        return gb >= 1 ? gb.ToString("0.0", Inv) + " GB" : Math.Round(gb * 1024).ToString("0", Inv) + " MB";
    }

    /// <summary>"5m", "2h 13m", "3d 4h" (the Mac's duration()).</summary>
    public static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes}m" : minutes < 1440 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes / 1440}d {minutes % 1440 / 60}h";

    /// <summary>Name as shown: our own plain-language names are translated, file descriptions are not.</summary>
    public static string NameOf(AppGroup g) => KnownProcesses.IsOwnName(g.Name) ? KnownProcesses.Display(g.Name) : g.Name;
    public static string ExplanationOf(AppGroup g) => KnownProcesses.Display(g.Explanation);

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

    /// <summary>The sentences as text only (tests, plain use).</summary>
    public static List<string> Lines(AppGroup g, int cpus, double ramTotal, DateTime now) => LinesWithIcons(g, cpus, ramTotal, now).Select(l => l.Text).ToList();

    /// <summary>The plain-language sentences about a group in the Mac's order (§4.9), each with its Segoe Fluent glyph; the verdict is separate.</summary>
    public static List<(string Glyph, string Text)> LinesWithIcons(AppGroup g, int cpus, double ramTotal, DateTime now)
    {
        var l = new List<(string, string)>();
        string name = NameOf(g);
        if (g.Explanation.Length > 0) l.Add(("\uE897", L("What is this? %@.", ExplanationOf(g))));
        l.Add(("\uE9F5", L("%@ is using %@ of your processor right now (≈ %@ of the %lld cores) and %@ of memory (%@ of your %lld GB).",
            name, Pct(g.Cpu), (g.Cpu / 100 * cpus).ToString("0.0", Culture), cpus, Mem(g.Ram),
            Pct(ramTotal > 0 ? g.Ram / ramTotal * 100 : 0), (long)Math.Round(ramTotal / 1073741824.0))));
        if (g.Kind == AppKind.Windows) l.Add(("\uE72E", L("Windows protects these parts, so they can't be stopped here.")));
        if (g.Pids.Count == 1) l.Add(("\uE8F1", L("It's a single process.")));
        else if (g.Pids.Count > 1)
            l.Add(("\uE8F1", g.Parts.Count > 0 ? L("It consists of %lld parts (processes), including %@.", g.Pids.Count, string.Join(", ", g.Parts))
                                              : L("It consists of %lld parts (processes).", g.Pids.Count)));
        if (g.Gpu >= 0.5) l.Add(("\uE7F4", L("Uses the graphics chip %@ of the time.", Pct(Math.Min(g.Gpu, 100)))));
        if (g.DiskRead + g.DiskWrite >= 1000) l.Add(("\uEDA2", L("Reads %@ and writes %@ (files, network and devices).", Fmt.Rate(g.DiskRead), Fmt.Rate(g.DiskWrite))));
        if (g.Threads > 0) l.Add(("\uE8AB", L("Runs %lld threads (tasks at the same time).", g.Threads)));
        if (g.StartedLocal is { } s) l.Add(("\uE917", L("Running for %@.", Duration(Math.Max((int)(now - s).TotalMinutes, 1)))));
        return l;
    }
}
