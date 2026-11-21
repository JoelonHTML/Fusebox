using System.Globalization;
using System.Text;

namespace Fusebox.Core;

/// <summary>
/// The Mac's CSV logs in Documents\Fusebox (FEATURES.md §11.1), same file names and Dutch column names so Checkup and old logs work:
/// meterkast-YYYY-MM-DD.csv (every sample), apps-…csv (per minute, <see cref="Processes.AppsLog"/>), peaks-…csv (<see cref="PeakWatch"/>),
/// battery-health.csv (once a day). Timestamps local "yyyy-MM-dd HH:mm:ss", always a dot as decimal separator.
/// Writing never throws (a full disk must not stop the meter); reading skips malformed lines.
/// </summary>
public static class CsvLog
{
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    public const string Header = "tijd,cpu_pct,ram_pct,ram_gb,geheugendruk,gpu_pct,temp_c,gpu_temp_c,down_Bps,up_Bps,ping_ms,schijf_vrij_gb,batterij_pct,schijf_totaal_gb,lezen_Bps,schrijven_Bps,swap_gb,ventilator_pct,wifi_dbm\n";
    public const string BatteryHeader = "datum,gezondheid_pct,cycli\n";
    public const string BatteryFile = "battery-health.csv";
    /// <summary>Day files removed by <see cref="Prune"/> (battery-health.csv is never pruned).</summary>
    public static readonly string[] DayPrefixes = ["meterkast", "apps", "peaks", "self"];
    const double GiB = 1073741824, GB = 1e9;

    public static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", Culture);

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fusebox");
    public static string FileFor(DateTime day, string prefix = "meterkast", string? dir = null) => Path.Combine(dir ?? Dir, $"{prefix}-{day:yyyy-MM-dd}.csv");

    // Required columns: non-finite → 0. Optional columns (temperatures, ping, battery, fan, Wi-Fi): empty when unknown.
    static string F1(double v) => (double.IsFinite(v) ? v : 0).ToString("0.0", Culture);
    static string F2(double v) => (double.IsFinite(v) ? v : 0).ToString("0.00", Culture);
    static string Opt1(double v) => double.IsFinite(v) ? v.ToString("0.0", Culture) : "";
    static string I(double v) => ((long)Math.Clamp(double.IsFinite(v) ? v : 0, 0, 1e15)).ToString(Culture);
    static string OptI(double v) => double.IsFinite(v) ? ((long)Math.Round(Math.Clamp(v, -1e15, 1e15))).ToString(Culture) : "";

    /// <summary>One main-log row. Windows has no CPU/GPU temperature or fans: those columns stay empty; swap unknown → 0.</summary>
    public static string Line(LiveSample s) => string.Join(',',
        Stamp(s.Time), F1(s.Cpu), F1(s.RamPercent), F1(s.RamUsed / GiB), s.Pressure.ToString(Culture), F1(s.Gpu),
        Opt1(s.TempC), Opt1(s.GpuTempC), I(s.NetIn), I(s.NetOut), Opt1(s.PingMs), F1(s.DiskFree / GB), Opt1(s.Battery),
        F1(s.DiskTotal / GB), I(s.DiskRead), I(s.DiskWrite), F2(s.SwapBytes / GiB), "", OptI(s.WifiDbm)) + "\n";

    /// <summary>Appends text (header first on a new file). False on failure: the caller keeps its buffer.</summary>
    public static bool Append(string file, string header, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file)) File.WriteAllText(file, header, new UTF8Encoding(false));
            File.AppendAllText(file, text, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Buffer cap after failed flushes: above 2 MB keep the last 1 MB (whole lines).</summary>
    public static void Cap(StringBuilder buffer, int max = 2_000_000, int keep = 1_000_000)
    {
        if (buffer.Length <= max) return;
        var s = buffer.ToString(buffer.Length - keep, keep);
        int nl = s.IndexOf('\n');
        buffer.Clear().Append(nl >= 0 ? s[(nl + 1)..] : s);
    }

    /// <summary>
    /// Today's file with another header (older Fusebox): columns remapped by name (aliases: old name → new), missing ones empty, no data
    /// lost. A file without any recognizable column is left alone. Returns true when rewritten.
    /// </summary>
    public static bool MigrateHeader(string file, string header, IReadOnlyDictionary<string, string>? aliases = null)
    {
        try
        {
            if (!File.Exists(file)) return false;
            var text = File.ReadAllText(file);
            var rewritten = Remap(text, header, aliases);
            if (rewritten == null) return false;
            File.WriteAllText(file + ".tmp", rewritten, new UTF8Encoding(false));
            File.Move(file + ".tmp", file, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Pure part of <see cref="MigrateHeader"/>: null = nothing to do.</summary>
    public static string? Remap(string text, string header, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var lines = text.Split('\n');
        var oldCols = lines[0].TrimEnd('\r').Split(',');
        var want = header.TrimEnd('\n').Split(',');
        if (lines[0].TrimEnd('\r') == header.TrimEnd('\n')) return null;
        var from = want.Select(c => Array.FindIndex(oldCols, o => o == c || (aliases != null && aliases.TryGetValue(o, out var n) && n == c))).ToArray();
        if (from.All(i => i < 0)) return null;
        var sb = new StringBuilder(header);
        foreach (var l in lines.Skip(1))
        {
            var v = l.TrimEnd('\r');
            if (v.Length == 0) continue;
            var cells = v.Split(',');
            sb.Append(string.Join(',', from.Select(i => i >= 0 && i < cells.Length ? cells[i] : ""))).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Retention (pref keepDays, 0 = keep forever): deletes day files whose filename date is before start-of-today − days.
    /// battery-health.csv is never deleted.
    /// </summary>
    public static int Prune(int days, DateTime now, string? dir = null)
    {
        if (days <= 0) return 0;
        int n = 0;
        try
        {
            var limit = now.Date.AddDays(-days);
            foreach (var f in Directory.EnumerateFiles(dir ?? Dir, "*.csv"))
                if (DayOf(Path.GetFileName(f)) is { } d && d < limit) { File.Delete(f); n++; }
        }
        catch { }
        return n;
    }

    /// <summary>"Delete all logs": every CSV in the folder.</summary>
    public static void DeleteAll(string? dir = null)
    {
        try { foreach (var f in Directory.EnumerateFiles(dir ?? Dir, "*.csv")) File.Delete(f); } catch { }
    }

    /// <summary>Date of a day file ("apps-2026-11-21.csv"), null for other files.</summary>
    public static DateTime? DayOf(string fileName)
    {
        foreach (var p in DayPrefixes)
            if (fileName.StartsWith(p + "-") && fileName.EndsWith(".csv")
                && DateTime.TryParseExact(fileName[(p.Length + 1)..^4], "yyyy-MM-dd", Culture, DateTimeStyles.None, out var d)) return d;
        return null;
    }

    /// <summary>battery-health.csv: one line per day ("datum,gezondheid_pct,cycli"). False when today is already there or no battery.</summary>
    public static bool AppendBatteryHealth(DateTime day, double healthPct, double cycles, string? dir = null)
    {
        if (!double.IsFinite(healthPct)) return false;
        var file = Path.Combine(dir ?? Dir, BatteryFile);
        var date = day.ToString("yyyy-MM-dd", Culture);
        try { if (File.Exists(file) && File.ReadLines(file).Any(l => l.StartsWith(date))) return false; } catch { return false; }
        return Append(file, BatteryHeader, $"{date},{healthPct.ToString("0.0", Culture)},{(double.IsFinite(cycles) ? ((long)cycles).ToString(Culture) : "")}\n");
    }

    // ---- reading ----

    /// <summary>Main log back as samples (for today's minute averages and Checkup). Columns by name; malformed rows skipped.</summary>
    public static List<LiveSample> ReadSamples(string csv)
    {
        var t = CsvTable.Parse(csv);
        var list = new List<LiveSample>(t.Rows.Count);
        int cpu = t.Col("cpu_pct");
        if (cpu < 0) return list;
        foreach (var r in t.Rows)
        {
            if (!t.Time(r, out var time) || !double.IsFinite(t.Num(r, cpu))) continue;
            double ramPct = t.Num(r, "ram_pct"), used = t.Num(r, "ram_gb") * GiB;
            list.Add(new LiveSample
            {
                Time = time, Cpu = t.Num(r, cpu), Gpu = t.Num(r, "gpu_pct"),
                RamUsed = used, RamTotal = ramPct > 0 ? used / (ramPct / 100) : double.NaN,
                Pressure = double.IsFinite(t.Num(r, "geheugendruk")) ? (int)t.Num(r, "geheugendruk") : 1,
                TempC = t.Num(r, "temp_c"), GpuTempC = t.Num(r, "gpu_temp_c"), PingMs = t.Num(r, "ping_ms"),
                NetIn = t.Num(r, "down_Bps"), NetOut = t.Num(r, "up_Bps"),
                DiskFree = t.Num(r, "schijf_vrij_gb") * GB, DiskTotal = t.Num(r, "schijf_totaal_gb") * GB,
                DiskRead = t.Num(r, "lezen_Bps"), DiskWrite = t.Num(r, "schrijven_Bps"),
                Battery = t.Num(r, "batterij_pct"), SwapBytes = t.Num(r, "swap_gb") * GiB, WifiDbm = t.Num(r, "wifi_dbm"),
            });
        }
        return list;
    }

    /// <summary>One minute of the main log as Checkup aggregates it (§5.4).</summary>
    public sealed record Minute(DateTime Time, double Cpu, double RamGb, int Pressure, double Temp, double DiskFreeGb, double DiskTotalGb,
        double Ping, bool Offline, bool WifiDrop, double Wifi, int Samples);

    /// <summary>
    /// §5.4 minute aggregation: mean CPU and RAM GB, highest pressure, mean temperature (non-empty only), last disk free/total, mean ping;
    /// offline = the ping column exists but fewer than half the samples have a ping; wifiDrop = any wifi_dbm 0; mean of negative Wi-Fi values.
    /// </summary>
    public static List<Minute> ReadMinutes(string csv)
    {
        var t = CsvTable.Parse(csv);
        var result = new List<Minute>();
        int cpu = t.Col("cpu_pct"), ping = t.Col("ping_ms"), wifi = t.Col("wifi_dbm");
        if (cpu < 0) return result;
        static double Mean(List<double> v) { var ok = v.Where(double.IsFinite).ToList(); return ok.Count == 0 ? double.NaN : ok.Average(); }
        foreach (var g in t.Rows.Where(r => r[0].Length >= 16 && double.IsFinite(t.Num(r, cpu))).GroupBy(r => r[0][..16]))
        {
            if (!DateTime.TryParseExact(g.Key, "yyyy-MM-dd HH:mm", Culture, DateTimeStyles.None, out var time)) continue;
            var rows = g.ToList();
            var pings = rows.Select(r => t.Num(r, ping)).ToList();
            var wifis = rows.Select(r => t.Num(r, wifi)).ToList();
            result.Add(new Minute(time,
                Mean(rows.Select(r => t.Num(r, cpu)).ToList()),
                Mean(rows.Select(r => t.Num(r, "ram_gb")).ToList()),
                rows.Select(r => t.Num(r, "geheugendruk")).Where(double.IsFinite).Select(v => (int)v).DefaultIfEmpty(1).Max(),
                Mean(rows.Select(r => t.Num(r, "temp_c")).ToList()),
                rows.Select(r => t.Num(r, "schijf_vrij_gb")).LastOrDefault(double.IsFinite, double.NaN),
                rows.Select(r => t.Num(r, "schijf_totaal_gb")).LastOrDefault(double.IsFinite, double.NaN),
                Mean(pings),
                ping >= 0 && pings.Count(double.IsFinite) * 2 < rows.Count,
                wifis.Any(v => v == 0),
                Mean(wifis.Where(v => v < 0).ToList()),
                rows.Count));
        }
        return result;
    }
}

/// <summary>A parsed CSV: column index by header name (first one wins when a name repeats, e.g. after editing in Excel).</summary>
public sealed class CsvTable
{
    public string[] Columns { get; private init; } = [];
    public List<string[]> Rows { get; } = [];
    readonly Dictionary<string, int> index = [];

    /// <param name="fallbackHeader">Used when the first line is not a header (old apps logs without one).</param>
    public static CsvTable Parse(string csv, string? fallbackHeader = null)
    {
        var lines = csv.Split('\n');
        var first = lines.Length > 0 ? lines[0].TrimEnd('\r').TrimStart('﻿') : "";
        bool hasHeader = first.Length > 0 && !char.IsDigit(first[0]);
        var cols = (hasHeader ? first : fallbackHeader?.TrimEnd('\n') ?? "").Split(',');
        var t = new CsvTable { Columns = cols };
        for (int i = 0; i < cols.Length; i++) t.index.TryAdd(cols[i], i);
        foreach (var l in lines.Skip(hasHeader ? 1 : 0))
        {
            var v = l.TrimEnd('\r');
            if (v.Length > 0) t.Rows.Add(v.Split(','));
        }
        return t;
    }

    /// <summary>Column index, −1 when missing.</summary>
    public int Col(string name) => index.TryGetValue(name, out var i) ? i : -1;

    /// <summary>Number in a cell; NaN when missing, empty, "nan"/"inf" or not a number.</summary>
    public double Num(string[] row, int col) =>
        col >= 0 && col < row.Length && double.TryParse(row[col], NumberStyles.Float, CsvLog.Culture, out var d) && double.IsFinite(d) ? d : double.NaN;

    public double Num(string[] row, string name) => Num(row, Col(name));

    public string Text(string[] row, string name) { int c = Col(name); return c >= 0 && c < row.Length ? row[c] : ""; }

    /// <summary>First column as "yyyy-MM-dd HH:mm:ss" (seconds optional).</summary>
    public bool Time(string[] row, out DateTime t) =>
        DateTime.TryParseExact(row[0], ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm"], CsvLog.Culture, DateTimeStyles.None, out t);
}

/// <summary>Charge-time estimate when Windows gives none (Mac Meter.timeToFull): rate of the last 5–15 minutes.</summary>
public sealed class BatteryEta
{
    readonly List<(DateTime t, double pct)> history = [];

    public void Add(DateTime t, double pct)
    {
        if (double.IsNaN(pct)) return;
        history.Add((t, pct));
        history.RemoveAll(h => (t - h.t).TotalSeconds > 1800);
    }

    /// <summary>Minutes until 100%, or NaN.</summary>
    public double ToFull(DateTime now, double pct)
    {
        if (double.IsNaN(pct) || pct >= 100) return double.NaN;
        var old = history.LastOrDefault(h => (now - h.t).TotalSeconds >= 300);
        if (old.t == default || (now - old.t).TotalSeconds > 900) return double.NaN;
        double perMin = (pct - old.pct) / (now - old.t).TotalMinutes;
        return perMin > 0.05 ? Math.Round((100 - pct) / perMin) : double.NaN;
    }

    /// <summary>"1 h 20 min" style.</summary>
    public static string Duration(double minutes)
    {
        if (double.IsNaN(minutes)) return "–";
        int m = (int)Math.Round(minutes);
        return m >= 60 ? $"{m / 60} h {m % 60} min" : $"{m} min";
    }
}
