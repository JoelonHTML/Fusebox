namespace Fusebox.Core.Processes;

/// <summary>An installed app found for the search ("Apps" section). Path = an exe when known (icon, About), else null.</summary>
public sealed record InstalledApp(string Name, string? Path);

public enum Col { Cpu, Gpu, Ram, Disk, Threads, Procs, Running }

/// <summary>The list's pure rules (FEATURES.md §4.4–4.8): chip filter, search, small items, columns per width band, sort ids.</summary>
public static class ProcessList
{
    /// <summary>Stored chip id: all / apps / background / macos (the "Windows" chip keeps the Mac id).</summary>
    public static bool MatchesFilter(AppGroup g, string filter) => filter switch
    {
        "apps" => g.Kind == AppKind.App,
        "background" => g.Kind == AppKind.Background,
        "macos" => g.Kind == AppKind.Windows,
        _ => true,
    };

    /// <summary>Case-insensitive "contains" on the name OR the explanation (empty query matches everything).</summary>
    public static bool MatchesSearch(string name, string explanation, string query) =>
        query.Length == 0 || name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                          || explanation.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>procSmall: all (nothing hidden) / idle (cpu &lt; 0.05 % and &lt; 20 MB) / light (cpu &lt; 1 % and &lt; 200 MB). A row stays when either reaches its limit.</summary>
    public static bool IsSmall(AppGroup g, string mode)
    {
        if (g.Kind == AppKind.Windows) return false;   // "Windows itself" is the leftover; never hide it
        return mode switch
        {
            "idle" => g.Cpu < 0.05 && g.Ram < 20 * 1048576.0,
            "light" => g.Cpu < 1 && g.Ram < 200 * 1048576.0,
            _ => false,
        };
    }

    public static AppGrouper.SortBy SortOf(string id) => id switch
    {
        "ram" => AppGrouper.SortBy.Ram, "gpu" => AppGrouper.SortBy.Gpu, "name" => AppGrouper.SortBy.Name, "disk" => AppGrouper.SortBy.Disk,
        "threads" => AppGrouper.SortBy.Threads, "running" => AppGrouper.SortBy.Running, _ => AppGrouper.SortBy.Cpu,
    };

    /// <summary>Visible extra columns and their widths for a list width. Name, CPU and RAM always; the rest by band and <c>procHide</c>.
    /// Energy and Network are never offered on Windows (no honest per-process source), so their hide ids are simply ignored.</summary>
    public static List<(Col Col, double Width)> Columns(double width, IReadOnlyCollection<string> hidden)
    {
        bool Show(string id) => !hidden.Contains(id);
        bool procs = width >= 700;
        var c = new List<(Col, double)> { (Col.Cpu, procs ? 90 : 58) };
        if (width >= 560 && Show("gpu")) c.Add((Col.Gpu, 44));
        c.Add((Col.Ram, 62));
        if (width >= 460 && Show("disk")) c.Add((Col.Disk, 70));
        if (procs && Show("threads")) { c.Add((Col.Threads, 54)); c.Add((Col.Procs, 40)); }
        if (width >= 820 && Show("running")) c.Add((Col.Running, 62));
        return c;
    }

    /// <summary>Installed apps for a query: not already running (by name), prefix matches first, then the rest, max <paramref name="max"/>.</summary>
    public static List<InstalledApp> Installed(IEnumerable<InstalledApp> installed, IEnumerable<string> runningNames, string query, int max = 30)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var running = new HashSet<string>(runningNames, StringComparer.CurrentCultureIgnoreCase);
        var hits = installed.Where(a => !running.Contains(a.Name) && a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First());
        return hits.OrderBy(a => a.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Take(max).ToList();
    }
}

/// <summary>What the app page's History shows, computed from the apps-*.csv rows of one app (FEATURES.md §4.11.3).</summary>
public sealed record AppUsage(int Minutes, double AvgCpu, double PeakCpu, double AvgMemGb, double AvgGpu, bool HasGpu, double[] ByHour, double[] ByWeekday)
{
    public static readonly AppUsage Empty = new(0, 0, 0, 0, 0, false, new double[24], new double[7]);

    /// <summary>The log writes commas as spaces; compare names the same way.</summary>
    public static string LogName(string name) => name.Replace(',', ' ').Replace('\n', ' ');

    /// <summary>Reads the last <paramref name="days"/> apps-*.csv files (today included) and computes the usage of one app. Off the UI thread.</summary>
    public static AppUsage Load(string name, int days, DateTime now, string? dir = null)
    {
        var rows = new List<AppsLog.Row>();
        for (int d = 0; d < Math.Max(days, 1); d++)
        {
            var file = CsvLog.FileFor(now.Date.AddDays(-d), "apps", dir);
            try
            {
                if (!File.Exists(file)) continue;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var rd = new StreamReader(fs);
                rows.AddRange(AppsLog.Parse(rd.ReadToEnd()));
            }
            catch { /* unreadable day: skip it */ }
        }
        return Compute(rows, name, now.Date.AddDays(-(Math.Max(days, 1) - 1)));
    }

    /// <summary>Rows of <paramref name="name"/> since <paramref name="since"/>. Averages are over logged minutes; "time open" = logged minutes
    /// (every app with a window is logged every minute); bars = minutes per hour of day / per weekday (Monday first).</summary>
    public static AppUsage Compute(IEnumerable<AppsLog.Row> rows, string name, DateTime since)
    {
        string want = LogName(name);
        var seen = new HashSet<DateTime>();
        var hour = new double[24];
        var day = new double[7];
        double cpu = 0, mem = 0, gpu = 0, peak = 0;
        int gpuN = 0;
        foreach (var r in rows)
        {
            if (r.Time < since || !string.Equals(r.App, want, StringComparison.CurrentCultureIgnoreCase) || !seen.Add(r.Time)) continue;
            cpu += r.Cpu; mem += r.MemGb; peak = Math.Max(peak, r.Cpu);
            if (r.Gpu > 0) { gpu += r.Gpu; gpuN++; }
            hour[r.Time.Hour]++;
            day[((int)r.Time.DayOfWeek + 6) % 7]++;
        }
        int n = seen.Count;
        return n == 0 ? Empty : new AppUsage(n, cpu / n, peak, mem / n, gpuN == 0 ? 0 : gpu / n, gpuN > 0, hour, day);
    }
}
