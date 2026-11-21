using static Fusebox.Core.Strings;

namespace Fusebox.Core.Checkup;

/// <summary>"What Windows is doing" (§5.7.3 / 5.7.4): jobs that make the PC busy and pass by themselves. Matching is by app/process name.</summary>
public static class Housekeeping
{
    /// <summary>Busy = at least 25 % of one core.</summary>
    public const double BusyCoreShare = 25;

    public sealed record Job(string Text, string[] Names);

    // Names are matched case-insensitively as substrings of the app name (apps log) or process image name.
    public static readonly Job[] Jobs =
    [
        new("Windows Search is indexing", ["searchindexer", "search indexer", "windows search"]),
        new("OneDrive is syncing", ["onedrive"]),
        new("Windows Update is installing updates", ["tiworker", "modules installer", "mousocoreworker", "usocoreworker", "update orchestrator", "wuauclt"]),
        new("Microsoft Defender is scanning", ["msmpeng", "antimalware service", "mpdefendercoreservice"]),
    ];

    /// <summary>The translated job text (the text is the translation key).</summary>
    public static string Tr(string job) => L(job);

    public static string? JobFor(string name)
    {
        foreach (var j in Jobs)
            foreach (var n in j.Names)
                if (name.Contains(n, StringComparison.OrdinalIgnoreCase)) return j.Text;
        return null;
    }

    /// <summary>Busy jobs now, from (name, CPU % of the PC) of the last minute; in table order.</summary>
    public static List<string> Busy(IEnumerable<(string Name, double Cpu)> apps, int cores) =>
        apps.Where(a => a.Cpu * Math.Max(cores, 1) >= BusyCoreShare).Select(a => JobFor(a.Name)).Where(j => j != null).Select(j => j!)
            .Distinct().OrderBy(j => Array.FindIndex(Jobs, x => x.Text == j)).ToList();
}
