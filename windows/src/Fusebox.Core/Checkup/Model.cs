namespace Fusebox.Core.Checkup;

public enum Level { Good, Warning, Bad }

public sealed record Finding(Level Level, string Text)
{
    public int Penalty => Level switch { Level.Bad => 35, Level.Warning => 15, _ => 0 };
}

/// <summary>Advice with an optional button that opens a Settings page (ms-settings:…).</summary>
public sealed record Tip(string Text, string? Uri = null, string? Button = null);

public sealed record Category(string Id, string Name, int Score, List<Finding> Findings, List<Tip> Tips);

public sealed record DriveFree(string Name, long Free, long Total)
{
    public double FreePct => Total <= 0 ? 100 : Free * 100.0 / Total;
}

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public sealed record StartupItem(string Name, string Command, StartupSource Source, bool Enabled)
{
    /// <summary>Only per-user entries can be switched without administrator rights.</summary>
    public bool CanToggle => Source is StartupSource.UserRun or StartupSource.UserFolder;
}

public sealed record EventInfo(DateTime Time, string Provider, int Id, List<(string? Name, string Value)> Data)
{
    public string? Get(string name) => Data.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
    public string? At(int i) => i < Data.Count ? Data[i].Value : null;
}

public sealed record CrashStats(int AppCrashes, int Hangs, int BlueScreens, int UnexpectedShutdowns, List<(string App, int Count)> TopApps)
{
    public int Total => AppCrashes + Hangs + BlueScreens + UnexpectedShutdowns;
}

public sealed record WakeEvent(DateTime Time, string Source);
public sealed record SleepSummary(List<WakeEvent> Wakes, int Sleeps)
{
    /// <summary>Wakes between 00:00 and 06:00.</summary>
    public int NightWakes => Wakes.Count(w => w.Time.Hour < 6);
}

public sealed record BatteryHealth(int? Percent, int? Cycles);
public sealed record DiskInfo(string Name, string MediaType, string Health, long Size);
public sealed record DiskReliability(string Disk, int? Wear, double? Temperature, long? ReadErrors, long? PowerOnHours);
public sealed record WakeRequest(string Section, string Who, string Reason);

/// <summary>Everything the score needs. Null = not measured (yet) or not present on this PC.</summary>
public sealed class CheckupInputs
{
    public double? CpuAvg, CpuMax;                 // % over the Live history
    public double? RamPct, CommitPct;
    public List<DriveFree>? Drives;
    public int? StartupEnabled;
    public bool? PendingReboot;
    public double? UptimeDays;
    public CrashStats? Crashes;
    public double? StabilityIndex;                 // 1–10
    public BatteryHealth? Battery;                 // null = no battery
    public SleepSummary? Sleep;
    public List<DiskInfo>? Disks;
    public List<DiskReliability>? Ssd;             // only after the admin check
}

public sealed record CheckupResult(int Score, List<Category> Categories);
