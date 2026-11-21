using static Fusebox.Core.Strings;

namespace Fusebox.Core.Checkup;

public enum Period { Today, Yesterday, Week }

/// <summary>§5.3: days read (0 = today), the period compared with, and the phrases the texts use.</summary>
public static class Periods
{
    public static string Id(Period p) => p switch { Period.Today => "today", Period.Yesterday => "yesterday", _ => "week" };
    public static Period Parse(string? id, Period fallback) => id switch { "today" => Period.Today, "yesterday" => Period.Yesterday, "week" => Period.Week, _ => fallback };
    public static int[] Days(Period p) => p switch { Period.Today => [0], Period.Yesterday => [1], _ => [6, 5, 4, 3, 2, 1, 0] };
    public static int[] PrevDays(Period p) => p switch { Period.Today => [1], Period.Yesterday => [2], _ => [13, 12, 11, 10, 9, 8, 7] };
    public static string Title(Period p) => p switch { Period.Today => L("Today"), Period.Yesterday => L("Yesterday"), _ => L("7 days") };
    public static string PreviousName(Period p) => p switch { Period.Today => L("yesterday"), Period.Yesterday => L("the day before"), _ => L("the week before") };
    public static string When(Period p) => p switch { Period.Today => L("today"), Period.Yesterday => L("yesterday"), _ => L("in the last 7 days") };
    /// <summary>Start (inclusive) and end (exclusive) of the period.</summary>
    public static (DateTime From, DateTime To) Range(Period p, DateTime now) =>
        (now.Date.AddDays(-Days(p).Max()), now.Date.AddDays(-Days(p).Min() + 1));
    public static string FileName(Period p) => p switch { Period.Today => "today", Period.Yesterday => "yesterday", _ => "7-days" };
}

public sealed record AppStat(string Name, double Cpu, double MemGb, int Minutes, double PeakCpu, double PeakMemGb);
public sealed record BusyMoment(DateTime Start, DateTime End, double Cpu, string? App);
public sealed record LeakInfo(string App, double From, double To, int Hours, DateTime Start, DateTime End)
{
    public double PerHour => (To - From) / Math.Max(Hours, 1);
}
public sealed record RunawayInfo(string App, int Minutes, DateTime Start, DateTime End);
public sealed record JobStat(string Job, int Minutes);
public sealed record LogEvent(DateTime Start, DateTime End, int Level, string Glyph, string Text);
public sealed record LogFile(string Name, long Bytes);

/// <summary>One crashing app in the period (Event Log 1000 / 1002, WER).</summary>
public sealed record AppCrash(string App, int Crashes, int Hangs, int LoopCrashes)
{
    public bool Loop => LoopCrashes >= 5;
}

public sealed record CrashSummary(List<AppCrash> Apps, int OutOfMemory, int BlueScreens, int UnexpectedShutdowns)
{
    public static readonly CrashSummary None = new([], 0, 0, 0);
    public int Crashed => Apps.Count(a => a.Crashes > 0);
    public int Hung => Apps.Count(a => a.Hangs > 0);
    public bool Any => Apps.Count > 0 || OutOfMemory > 0 || BlueScreens > 0 || UnexpectedShutdowns > 0;
}

/// <summary>Everything the page shows for one period (§5.4 "Report fields"). Pure data: built by <see cref="Analysis.Build"/>.</summary>
public sealed class Report
{
    public Period Period;
    public DateTime Made;
    public int Minutes, BusyMin, PressureMin, HotMin, SlowPingMin, WifiDrops, WeakWifiMin;
    public DateTime? FirstMinute;
    public double DiskFreeGb = double.NaN, DiskTotalGb = double.NaN;
    public bool DiskLow => DiskTotalGb > 0 && DiskFreeGb / DiskTotalGb < 0.10;
    public int? Score, PrevScore;
    public string Reason = "";
    public List<AppStat> TopCpu = [], TopRam = [], SeenLongest = [], All = [];
    public List<BusyMoment> Busy = [];
    public List<Peak> Peaks = [];
    public List<LogEvent> Events = [];
    public List<LeakInfo> Leaks = [];
    public List<RunawayInfo> Runaways = [];
    public List<JobStat> Jobs = [];
    public CrashSummary Crashes = CrashSummary.None;
    public List<LogFile> Files = [];
    /// <summary>Runs of ≥ 5 minutes of one housekeeping job (gap ≤ 10 min).</summary>
    public List<(string Job, DateTime Start, DateTime End)> JobRuns = [];
    public double CpuAvg = double.NaN;

    public string When => Periods.When(Period);
    public bool HasApps => All.Count > 0;
}

/// <summary>Number and time text used by the cards (Mac helpers duration / mins / span / pct / one).</summary>
public static class CheckupFmt
{
    public static string Pct(double v) => Fmt.Pct(v);
    public static string One(double v) => Fmt.One(v);
    public static string Duration(int minutes) => Fmt.Duration(minutes);
    /// <summary>"40 min" below 60, else duration.</summary>
    public static string Mins(int m) => m < 60 ? $"{m} min" : Fmt.Duration(m);

    /// <summary>"Tue 14:20–14:35"; start only when the span is ≤ 60 s.</summary>
    public static string Span(DateTime a, DateTime b)
    {
        var c = Culture;
        var start = a.ToString("ddd HH:mm", c);
        return (b - a).TotalSeconds <= 60 ? start : start + "–" + b.ToString("HH:mm", c);
    }

    /// <summary>"Tue 23:10" (sleep rows).</summary>
    public static string Day(DateTime t) => t.ToString("ddd HH:mm", Culture);

    /// <summary>Capitalises the first letter.</summary>
    public static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Culture) + s[1..];

    /// <summary>Speed "3.1 GB/s" / "850 MB/s".</summary>
    public static string Speed(double mbps) => mbps >= 1000 ? $"{One(mbps / 1000)} GB/s" : $"{Math.Round(mbps)} MB/s";
}
