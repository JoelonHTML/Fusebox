namespace Fusebox.Core;

/// <summary>One reading of everything on the Live page. NaN = not available on this PC.</summary>
public sealed class LiveSample
{
    public DateTime Time { get; init; } = DateTime.Now;
    public double Cpu { get; set; } = double.NaN;            // 0–100
    public double[] Cores { get; set; } = [];                // 0–100 per logical processor
    public double RamUsed { get; set; } = double.NaN;        // bytes
    public double RamTotal { get; set; } = double.NaN;       // bytes
    public double NetIn { get; set; } = double.NaN;          // bytes/s, all physical adapters
    public double NetOut { get; set; } = double.NaN;
}
