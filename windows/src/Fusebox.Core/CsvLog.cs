using System.Globalization;
using System.Text;

namespace Fusebox.Core;

/// <summary>
/// One CSV per day in Documents\Fusebox (meterkast-YYYY-MM-DD.csv, peaks-YYYY-MM-DD.csv), same columns as the Mac's Sample.csv
/// (empty = not available on Windows/this PC). Lines are buffered and written once a minute by the caller.
/// </summary>
public static class CsvLog
{
    public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    public const string Header = "tijd,cpu_pct,ram_pct,ram_gb,geheugendruk,gpu_pct,temp_c,gpu_temp_c,down_Bps,up_Bps,ping_ms,schijf_vrij_gb,batterij_pct,schijf_totaal_gb,lezen_Bps,schrijven_Bps,swap_gb,ventilator_pct,wifi_dbm\n";

    public static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", Culture);

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fusebox");
    public static string FileFor(DateTime day, string prefix = "meterkast", string? dir = null) => Path.Combine(dir ?? Dir, $"{prefix}-{day:yyyy-MM-dd}.csv");

    static string F(double v) => double.IsFinite(v) ? v.ToString("0.0", Culture) : "";
    static string I(double v) => double.IsFinite(v) ? ((long)Math.Clamp(v, 0, 1e15)).ToString(Culture) : "";
    const double GB = 1e9;

    public static string Line(LiveSample s) =>
        $"{Stamp(s.Time)},{F(s.Cpu)},{F(s.RamPercent)},{F(s.RamUsed / GB)},,{F(s.Gpu)},,,{I(s.NetIn)},{I(s.NetOut)},,{F(s.DiskFree / GB)},{F(s.Battery)},{F(s.DiskTotal / GB)},{I(s.DiskRead)},{I(s.DiskWrite)},,,\n";

    /// <summary>Appends text (with header on a new file). Never throws: a full disk must not stop the meter.</summary>
    public static void Append(string file, string header, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file)) File.WriteAllText(file, header, new UTF8Encoding(false));
            File.AppendAllText(file, text, new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>Reads a day's CSV back as minute averages of one column (so "Today" is not empty after a restart).</summary>
    public static List<double> MinuteAverages(string csv, string column)
    {
        var result = new List<double>();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return result;
        int col = Array.IndexOf(lines[0].Trim().Split(','), column);
        if (col < 0) return result;
        string key = ""; double sum = 0; int n = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            var v = lines[i].TrimEnd('\r').Split(',');
            if (v[0].Length < 16) continue;
            var k = v[0][..16];
            if (k != key) { if (n > 0) result.Add(sum / n); key = k; sum = 0; n = 0; }
            if (col < v.Length && double.TryParse(v[col], NumberStyles.Float, Culture, out var d)) { sum += d; n++; }
        }
        if (n > 0) result.Add(sum / n);
        return result;
    }

    /// <summary>Deletes logs older than <paramref name="days"/> (0 = keep everything).</summary>
    public static void Prune(int days, string? dir = null)
    {
        if (days <= 0) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir ?? Dir, "*.csv"))
                if ((DateTime.Now - File.GetLastWriteTime(f)).TotalDays > days) File.Delete(f);
        }
        catch { }
    }
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
