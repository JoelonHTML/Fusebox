namespace Fusebox.Core.Processes;

/// <summary>Turns cumulative CPU times into % of the whole PC: Δcpu / (Δwall × logical CPUs). Keyed by pid + create time so reused pids start over.</summary>
public sealed class CpuTracker
{
    Dictionary<(int, long), long> prev = [];
    long prevWall = -1;

    public static double Percent(long deltaCpu, long deltaWall, int cpus) =>
        deltaWall <= 0 || cpus <= 0 || deltaCpu < 0 ? 0 : Math.Clamp(100.0 * deltaCpu / ((double)deltaWall * cpus), 0, 100);

    /// <summary>wall100ns: any monotonic clock in 100 ns units. First call returns zeros.</summary>
    public Dictionary<int, double> Update(IReadOnlyList<ProcSample> procs, long wall100ns, int cpus)
    {
        var result = new Dictionary<int, double>(procs.Count);
        var next = new Dictionary<(int, long), long>(procs.Count);
        long dw = prevWall < 0 ? 0 : wall100ns - prevWall;
        foreach (var p in procs)
        {
            var k = (p.Pid, p.CreateTime);
            next[k] = p.CpuTime;
            result[p.Pid] = dw > 0 && prev.TryGetValue(k, out var old) ? Percent(p.CpuTime - old, dw, cpus) : 0;
        }
        prev = next; prevWall = wall100ns;
        return result;
    }
}

public static class GpuInstance
{
    /// <summary>"pid_1234_luid_0x…_phys_0_eng_0_engtype_3D" → (1234, "3D"). False for names without a pid.</summary>
    public static bool TryParse(string instance, out int pid, out string engineType)
    {
        pid = 0; engineType = "";
        if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return false;
        int end = instance.IndexOf('_', 4);
        if (end < 0 || !int.TryParse(instance.AsSpan(4, end - 4), out pid)) return false;
        int t = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        engineType = t < 0 ? "" : instance[(t + 8)..];
        return true;
    }

    /// <summary>Per pid: sum the engines of one type, then take the busiest type (what Task Manager shows). Clamped to 100.</summary>
    public static Dictionary<int, double> PerPid(IEnumerable<(string Instance, double Value)> values)
    {
        var sums = new Dictionary<(int, string), double>();
        foreach (var (name, v) in values)
            if (TryParse(name, out var pid, out var type) && v > 0) sums[(pid, type)] = sums.GetValueOrDefault((pid, type)) + v;
        var result = new Dictionary<int, double>();
        foreach (var ((pid, _), v) in sums) result[pid] = Math.Min(100, Math.Max(result.GetValueOrDefault(pid), v));
        return result;
    }
}
