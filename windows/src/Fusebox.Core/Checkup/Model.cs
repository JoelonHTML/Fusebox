namespace Fusebox.Core.Checkup;

public sealed record DriveFree(string Name, long Free, long Total)
{
    public double FreePct => Total <= 0 ? 100 : Free * 100.0 / Total;
}

/// <summary>One event from the Windows Event Log (EvtRender XML parsed by <see cref="CheckupParsers.ParseEvent"/>).</summary>
public sealed record EventInfo(DateTime Time, string Provider, int Id, List<(string? Name, string Value)> Data)
{
    public string? Get(string name) => Data.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
    public string? At(int i) => i < Data.Count ? Data[i].Value : null;
}

public sealed record WakeEvent(DateTime Time, string Source);
public sealed record SleepSummary(List<WakeEvent> Wakes, int Sleeps)
{
    /// <summary>Wakes between 00:00 and 06:00.</summary>
    public int NightWakes => Wakes.Count(w => w.Time.Hour < 6);
}

/// <summary>Capacity as % of the design capacity plus the cycle count; either can be unknown.</summary>
public sealed record BatteryHealth(int? Percent, int? Cycles)
{
    /// <summary>Mac: worn = under 80 % of the original capacity.</summary>
    public bool Worn => Percent is < 80;
}

public sealed record DiskInfo(string Name, string MediaType, string Health, long Size);
public sealed record DiskReliability(string Disk, int? Wear, double? Temperature, long? ReadErrors, long? PowerOnHours);
public sealed record WakeRequest(string Section, string Who, string Reason);
