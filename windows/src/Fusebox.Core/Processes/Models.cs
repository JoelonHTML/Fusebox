namespace Fusebox.Core.Processes;

public enum AppKind { App, Background, Windows }

/// <summary>One row of NtQuerySystemInformation(SystemProcessInformation). Times are 100 ns units (FILETIME-style).</summary>
public readonly record struct ProcSample(int Pid, int ParentPid, string Name, long CpuTime, long CreateTime, long WorkingSet, int Threads);

/// <summary>A user-visible item: an app with all its helper processes rolled up, a background program, or "Windows".</summary>
public sealed class AppGroup
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? ExePath { get; init; }
    public string ImageName { get; init; } = "";
    public AppKind Kind { get; set; }
    public List<int> Pids { get; init; } = [];
    public double Cpu { get; set; }          // % of the whole PC (Task Manager style)
    public double Gpu { get; set; }          // % of the busiest engine
    public long Ram { get; set; }            // bytes (private working set)
    public int Threads { get; set; }
    public long Started { get; set; }        // earliest FILETIME of the group
    public string Explanation { get; set; } = "";
    public bool CanStop { get; set; }
    public DateTime? StartedLocal => Started > 0 ? DateTime.FromFileTime(Started) : null;
}

public sealed record ProcessSnapshot(IReadOnlyList<AppGroup> Groups, int ProcessCount, double OwnCpu, long OwnRam, int Cpus, DateTime Time);
