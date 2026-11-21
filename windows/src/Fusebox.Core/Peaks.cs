namespace Fusebox.Core;

public enum PeakKind { Cpu, Gpu, Mem }

public sealed class Peak
{
    public PeakKind Kind { get; init; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public double Top { get; set; }
    public DateTime At { get; set; }
    public int Seconds => (int)Math.Round((End - Start).TotalSeconds);
    /// <summary>Biggest cause; filled in by the Processes area when it has a snapshot.</summary>
    public string? App { get; set; }
}

/// <summary>Follows one kind of measurement; pure (unit-tested). Port of the Mac's PeakTracker.</summary>
public sealed class PeakTracker(PeakKind kind, int minLength = 2, int gap = 1)
{
    Peak? open;
    int count, below;

    /// <summary>Running peak, once it is long enough.</summary>
    public Peak? Current => count >= minLength ? open : null;

    /// <summary>Started: first reading above the limit; Closed: a peak that just ended (long enough).</summary>
    public (bool Started, Peak? Closed) Feed(DateTime t, double v, double level, double stepSeconds = 2)
    {
        var step = TimeSpan.FromSeconds(stepSeconds);
        if (v >= level)
        {
            below = 0;
            if (open is not { } p)
            {
                open = new Peak { Kind = kind, Start = t, End = t + step, Top = v, At = t };
                count = 1;
                return (true, null);
            }
            p.End = t + step; count++;
            if (v > p.Top) { p.Top = v; p.At = t; }
            return (false, null);
        }
        if (open is not { } o) return (false, null);
        if (++below <= gap) return (false, null);
        open = null; var n = count; count = 0; below = 0;
        return (false, n >= minLength ? o : null);
    }
}

/// <summary>Sustained high CPU/GPU/RAM. Feed every sample; closed peaks pile up in <see cref="Today"/> and raise <see cref="Closed"/>.</summary>
public sealed class PeakWatch
{
    public const double Busy = 80, MemHigh = 90;
    public const string CsvHeader = "time,end,kind,top,app\n";
    readonly Dictionary<PeakKind, PeakTracker> trackers = new()
    {
        [PeakKind.Cpu] = new(PeakKind.Cpu), [PeakKind.Gpu] = new(PeakKind.Gpu), [PeakKind.Mem] = new(PeakKind.Mem, 1),
    };
    DateTime day;

    public List<Peak> Today { get; } = [];
    public event Action<Peak>? Closed;

    public static double Level(PeakKind k) => k == PeakKind.Mem ? MemHigh : Busy;

    public void Feed(LiveSample s, double stepSeconds = 2)
    {
        if (s.Time.Date != day) { day = s.Time.Date; Today.Clear(); }
        Feed(PeakKind.Cpu, s.Time, s.Cpu, stepSeconds);
        Feed(PeakKind.Gpu, s.Time, s.Gpu, stepSeconds);
        Feed(PeakKind.Mem, s.Time, s.RamPercent, stepSeconds);
    }

    void Feed(PeakKind k, DateTime t, double v, double step)
    {
        if (double.IsNaN(v)) return;
        var (_, closed) = trackers[k].Feed(t, v, Level(k), step);
        if (closed != null) { Today.Add(closed); Closed?.Invoke(closed); }
    }

    public Peak? Running(PeakKind k) => trackers[k].Current;

    public static string CsvLine(Peak p) =>
        $"{CsvLog.Stamp(p.Start)},{CsvLog.Stamp(p.End)},{p.Kind.ToString().ToLowerInvariant()},{p.Top.ToString("0.0", CsvLog.Culture)},{p.App}\n";
}
