namespace Fusebox.Core;

/// <summary>One reading of everything on the Live page. NaN = not available on this PC.</summary>
public sealed class LiveSample
{
    public DateTime Time { get; init; } = DateTime.Now;
    public double Cpu { get; set; } = double.NaN;            // 0–100
    public double[] Cores { get; set; } = [];                // 0–100 per logical processor
    public double RamUsed { get; set; } = double.NaN;        // bytes
    public double RamTotal { get; set; } = double.NaN;       // bytes
    public double CommitUsed { get; set; } = double.NaN;     // bytes
    public double CommitLimit { get; set; } = double.NaN;    // bytes
    public double NetIn { get; set; } = double.NaN;          // bytes/s, all physical adapters
    public double NetOut { get; set; } = double.NaN;
    public double Gpu { get; set; } = double.NaN;            // 0–100, busiest engine type (Task Manager logic)
    public (string Engine, double Percent)[] GpuEngines { get; set; } = [];   // "3D", "Compute", "Copy", "VideoDecode" …
    public double GpuMemDedicated { get; set; } = double.NaN; // bytes in use
    public double GpuMemShared { get; set; } = double.NaN;
    public double DiskRead { get; set; } = double.NaN;       // bytes/s, all physical disks
    public double DiskWrite { get; set; } = double.NaN;
    public double DiskFree { get; set; } = double.NaN;       // bytes, system drive
    public double DiskTotal { get; set; } = double.NaN;
    public double Battery { get; set; } = double.NaN;        // percent; NaN = no battery
    public bool OnAc { get; set; }                           // plugged in (also when full)
    public bool Charging { get; set; }
    public double BatteryMw { get; set; } = double.NaN;      // + charging, − discharging
    public double BatteryMinutes { get; set; } = double.NaN; // time left (on battery) or to full (charging)
    public double BatteryDesign { get; set; } = double.NaN;  // mWh
    public double BatteryFull { get; set; } = double.NaN;    // mWh at full charge now
    public double BatteryCycles { get; set; } = double.NaN;

    public double RamPercent => RamTotal > 0 ? 100 * RamUsed / RamTotal : double.NaN;
    public double BatteryHealth => BatteryDesign > 0 ? 100 * BatteryFull / BatteryDesign : double.NaN;
}

/// <summary>Read once at start: names and core classes.</summary>
public sealed class HardwareInfo
{
    public string CpuName { get; init; } = "";
    public string GpuName { get; init; } = "";
    public int PerformanceCores { get; init; }
    public int EfficiencyCores { get; init; }
    /// <summary>Per logical processor (same order as LiveSample.Cores): true = efficiency core.</summary>
    public bool[] EfficiencyLogical { get; init; } = [];
}
