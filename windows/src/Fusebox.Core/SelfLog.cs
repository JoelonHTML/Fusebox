using System.Globalization;
using System.Text;

namespace Fusebox.Core;

/// <summary>One minute of Fusebox's own use (self-YYYY-MM-DD.csv, §11.5). NaN = not measured on Windows (energy, GPU, wake-ups).</summary>
public sealed record SelfRow(DateTime Time, string Version, double Cpu, double MemMb, double Watt, double Gpu, double WsGpu, double Wakeups,
    string Page, string Window, bool Visible, string Theme, string Font, string LowImpact, string Power);

/// <summary>The per-minute own-usage log and the text of "Share performance report" (the Mac's SelfLog / EigenVerbruik).</summary>
public static class SelfLog
{
    public const string Header = "tijd,versie,cpu_pct,geheugen_mb,energie_w,gpu_pct,ws_gpu_pct,wakeups_s,pagina,venster,zichtbaar,thema,lettertype,low_impact,stroom\n";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string N(double v, string f) => double.IsFinite(v) ? v.ToString(f, Inv) : "";
    static string Clean(string s) => s.Replace(',', ' ').Replace('\n', ' ');

    public static string Line(SelfRow r) => string.Join(',', CsvLog.Stamp(r.Time), Clean(r.Version), N(r.Cpu, "0.00"), N(r.MemMb, "0"), N(r.Watt, "0.000"),
        N(r.Gpu, "0.00"), N(r.WsGpu, "0.00"), N(r.Wakeups, "0.0"), Clean(r.Page), Clean(r.Window), r.Visible ? "1" : "0",
        Clean(r.Theme), Clean(r.Font), Clean(r.LowImpact), Clean(r.Power)) + "\n";

    /// <summary>Rows of a self-*.csv; malformed lines are skipped.</summary>
    public static List<SelfRow> Parse(string csv)
    {
        var rows = new List<SelfRow>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var c = line.TrimEnd('\r').Split(',');
            if (c.Length < 15 || !DateTime.TryParseExact(c[0], "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out var t)) continue;
            double D(int i) => double.TryParse(c[i], NumberStyles.Float, Inv, out var v) ? v : double.NaN;
            rows.Add(new(t, c[1], D(2), D(3), D(4), D(5), D(6), D(7), c[8], c[9], c[10] == "1", c[11], c[12], c[13], c[14]));
        }
        return rows;
    }

    /// <summary>"small" / "medium" / "large" by window area (&lt; 250 000 px² / &lt; 700 000 px²), "-" = no window.</summary>
    public static string SizeBucket(string window)
    {
        var p = window.Split('x');
        if (p.Length != 2 || !double.TryParse(p[0], NumberStyles.Float, Inv, out var w) || !double.TryParse(p[1], NumberStyles.Float, Inv, out var h)) return "-";
        double a = w * h;
        return a < 250_000 ? "small" : a < 700_000 ? "medium" : "large";
    }

    public static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    static double Mean(IEnumerable<double> v) { var l = v.Where(double.IsFinite).ToList(); return l.Count == 0 ? double.NaN : l.Average(); }

    /// <summary>
    /// The report (§11.5): header, machine line, fixed-width table per situation (visible|page|size|theme|low:mode) sorted by minutes,
    /// then <paramref name="liveNow"/> (what Fusebox does in the 5 s measured right now). Nothing personal: no user or PC name.
    /// </summary>
    public static string Report(IReadOnlyList<SelfRow> rows, string version, string machine, string liveNow, int days = 7)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Fusebox performance report");
        sb.AppendLine($"Version {version} · {machine}");
        sb.AppendLine($"Last {days} day(s), {rows.Count} minutes measured. Fusebox's own use of one processor core; ws_gpu = whole-screen GPU (compositor), for context.");
        sb.AppendLine();
        sb.AppendLine($"{"situation",-44}{"min",6}{"cpu avg",9}{"cpu p95",9}{"mem MB",8}{"gpu %",7}{"ws_gpu",8}{"watt",7}{"wake/s",8}");
        string F(double v, string f) => double.IsFinite(v) ? v.ToString(f, Inv) : "-";
        foreach (var g in rows.GroupBy(r => $"{(r.Visible ? "visible" : "hidden")}|{r.Page}|{SizeBucket(r.Window)}|{r.Theme}|low:{r.LowImpact}").OrderByDescending(g => g.Count()))
        {
            var cpu = g.Select(r => r.Cpu).Where(double.IsFinite).Order().ToList();
            sb.AppendLine($"{g.Key,-44}{g.Count(),6}{F(Mean(g.Select(r => r.Cpu)), "0.00"),9}{F(Percentile(cpu, 0.95), "0.00"),9}{F(Mean(g.Select(r => r.MemMb)), "0"),8}"
                + $"{F(Mean(g.Select(r => r.Gpu)), "0.0"),7}{F(Mean(g.Select(r => r.WsGpu)), "0.0"),8}{F(Mean(g.Select(r => r.Watt)), "0.00"),7}{F(Mean(g.Select(r => r.Wakeups)), "0.0"),8}");
        }
        if (liveNow.Length > 0) { sb.AppendLine(); sb.AppendLine(liveNow); }
        return sb.ToString();
    }
}
