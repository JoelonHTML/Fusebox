namespace Fusebox.Core.Processes;

/// <summary>Groups processes into apps like Task Manager: windowed process + its helper children (and same-exe processes) = one app.</summary>
public static class AppGrouper
{
    static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "fontdrvhost",
        "dwm", "memory compression", "memcompression", "secure system", "lsaiso", "sihost", "ctfmon", "wudfhost",
    };

    // Never absorb children: apps started from these are their own apps.
    static readonly HashSet<string> NoAbsorb = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "cmd", "powershell", "pwsh", "conhost", "windowsterminal", "openconsole",
    };

    /// <summary>English name of the system group ("Windows itself"); show it with <see cref="ProcessText.NameOf"/>.</summary>
    public const string WindowsName = KnownProcesses.WindowsName;

    public static string Stem(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    public static bool IsSystem(ProcSample p) => p.Pid <= 4 || SystemNames.Contains(Stem(p.Name));

    public static List<AppGroup> Group(IReadOnlyList<ProcSample> procs, ISet<int> windowPids, Func<int, string?> exePath,
        Func<string?, string, string> displayName, IReadOnlyDictionary<int, double> cpu, IReadOnlyDictionary<int, double> gpu, int selfPid,
        IReadOnlyDictionary<int, (double Read, double Write)>? disk = null)
    {
        var byPid = new Dictionary<int, ProcSample>(procs.Count);
        foreach (var p in procs) byPid[p.Pid] = p;

        // Nearest windowed ancestor (or self); a process that has a window is its own root.
        int Root(ProcSample p)
        {
            if (windowPids.Contains(p.Pid)) return p.Pid;
            int best = p.Pid;
            var c = p;
            for (int hops = 0; hops < 16; hops++)
            {
                // parent must exist and be older than the child (pid reuse guard)
                if (!byPid.TryGetValue(c.ParentPid, out var par) || par.CreateTime > c.CreateTime || par.Pid == c.Pid) break;
                if (IsSystem(par) || NoAbsorb.Contains(Stem(par.Name))) break;
                if (windowPids.Contains(par.Pid)) { best = par.Pid; break; }
                c = par;
            }
            return best;
        }

        var groups = new Dictionary<string, AppGroup>();
        foreach (var p in procs)
        {
            if (p.Pid == 0) continue;
            string key;
            AppGroup? g;
            if (IsSystem(p))
            {
                key = "windows";
                if (!groups.TryGetValue(key, out g))
                    groups[key] = g = new AppGroup { Key = key, Name = WindowsName, Kind = AppKind.Windows, ImageName = "windows",
                        Explanation = KnownProcesses.Explain("windows") ?? "" };
            }
            else
            {
                var root = byPid[Root(p)];
                var path = exePath(root.Pid);
                key = (path ?? Stem(root.Name)).ToLowerInvariant();
                if (!groups.TryGetValue(key, out g))
                    groups[key] = g = new AppGroup { Key = key, Name = KnownProcesses.FriendlyName(Stem(root.Name)) ?? displayName(path, root.Name), ExePath = path, ImageName = Stem(root.Name),
                        Kind = AppKind.Background, Explanation = KnownProcesses.Explain(Stem(root.Name)) ?? "" };
                if (windowPids.Contains(root.Pid)) g.Kind = AppKind.App;
            }
            g.Pids.Add(p.Pid);
            g.Cpu += cpu.GetValueOrDefault(p.Pid);
            g.Gpu += gpu.GetValueOrDefault(p.Pid);
            g.Ram += p.WorkingSet;
            g.Threads += p.Threads;
            if (disk != null && disk.TryGetValue(p.Pid, out var d)) { g.DiskRead += d.Read; g.DiskWrite += d.Write; }
            if (g.Parts.Count < 3 && p.Name.Length > 0 && !g.Parts.Contains(Stem(p.Name), StringComparer.OrdinalIgnoreCase)) g.Parts.Add(Stem(p.Name));
            if (p.CreateTime > 0 && (g.Started == 0 || p.CreateTime < g.Started)) g.Started = p.CreateTime;
        }
        foreach (var g in groups.Values)
        {
            g.Gpu = Math.Min(100, g.Gpu);
            g.CanStop = g.Kind != AppKind.Windows && !g.Pids.Contains(selfPid);
        }
        return groups.Values.ToList();
    }

    /// <summary>The leftover "Windows itself" row (Mac "macOS itself"): total CPU / used memory minus what the apps account for, never below
    /// what the system processes measured themselves. The minute monitor and the list both go through the sampler, so they agree.</summary>
    public static void AddRemainder(IReadOnlyList<AppGroup> groups, double totalCpu, double usedRam)
    {
        var win = groups.FirstOrDefault(g => g.Kind == AppKind.Windows);
        if (win == null) return;
        double cpu = 0, ram = 0;
        foreach (var g in groups) if (g != win) { cpu += g.Cpu; ram += g.Ram; }
        if (double.IsFinite(totalCpu)) win.Cpu = Math.Max(win.Cpu, Math.Clamp(totalCpu - cpu, 0, 100));
        if (double.IsFinite(usedRam) && usedRam > 0) win.Ram = Math.Max(win.Ram, (long)Math.Max(0, usedRam - ram));
    }

    public enum SortBy { Cpu, Ram, Gpu, Name, Disk, Threads, Running }

    public static List<AppGroup> Sort(IEnumerable<AppGroup> groups, SortBy by, bool descending = true)
    {
        Func<AppGroup, IComparable> k = by switch
        {
            SortBy.Ram => g => g.Ram, SortBy.Gpu => g => g.Gpu, SortBy.Disk => g => g.DiskRead + g.DiskWrite, SortBy.Threads => g => g.Threads,
            SortBy.Running => g => g.Started > 0 ? -g.Started : long.MinValue,   // an earlier start counts as larger
            SortBy.Name => g => g.Name.ToLowerInvariant(), _ => g => g.Cpu,
        };
        // CPU ties by memory (the Mac's default order), the rest by name.
        var ordered = descending ? groups.OrderByDescending(k) : groups.OrderBy(k);
        return (by == SortBy.Cpu ? ordered.ThenByDescending(g => g.Ram) : ordered).ThenBy(g => g.Name.ToLowerInvariant()).ToList();
    }
}
