using System.Globalization;

namespace Fusebox.Core.Processes;

/// <summary>
/// The per-minute apps log apps-YYYY-MM-DD.csv (FEATURES.md §4.13), Mac columns. Feeds the app page history, Checkup and peak causes.
/// Rows: top 10 by CPU ∪ top 10 by memory ∪ every app with a window (so "time open" is right). Windows has no per-process watts:
/// energie_w and mac_energie_w are 0. soort: app / achtergrond (background) / macos (= the "Windows" group).
/// </summary>
public static class AppsLog
{
    public const string Header = "tijd,app,soort,cpu_pct,geheugen_gb,energie_w,gpu_pct,mac_energie_w\n";
    const double GiB = 1073741824;
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Kind(AppKind k) => k switch { AppKind.App => "app", AppKind.Background => "achtergrond", _ => "macos" };

    /// <summary>Which groups get a row this minute.</summary>
    public static List<AppGroup> Pick(IReadOnlyList<AppGroup> groups)
    {
        var keep = new HashSet<AppGroup>(groups.OrderByDescending(g => g.Cpu).Take(10));
        keep.UnionWith(groups.OrderByDescending(g => g.Ram).Take(10));
        keep.UnionWith(groups.Where(g => g.Kind == AppKind.App));
        return groups.Where(keep.Contains).ToList();
    }

    /// <summary>The lines for one minute (no header). <paramref name="groups"/> = one measurement with CPU averaged over the minute.</summary>
    public static string Lines(DateTime t, IReadOnlyList<AppGroup> groups)
    {
        var sb = new System.Text.StringBuilder();
        string stamp = CsvLog.Stamp(t);
        foreach (var g in Pick(groups))
            sb.Append(stamp).Append(',').Append(g.Name.Replace(',', ' ').Replace('\n', ' ')).Append(',').Append(Kind(g.Kind)).Append(',')
              .Append(Safe(g.Cpu).ToString("0.0", C)).Append(',').Append((g.Ram / GiB).ToString("0.00", C)).Append(",0.00,")
              .Append(Safe(g.Gpu).ToString("0.0", C)).Append(",0.00\n");
        return sb.ToString();
    }

    static double Safe(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1e6) : 0;

    /// <summary>One row read back.</summary>
    public sealed record Row(DateTime Time, string App, string Kind, double Cpu, double MemGb, double EnergyW, double Gpu, double MacEnergyW);

    /// <summary>Parses an apps log; files without a header use the fixed Mac order tijd,app,soort,cpu_pct,geheugen_gb,energie_w.</summary>
    public static List<Row> Parse(string csv)
    {
        var t = CsvTable.Parse(csv, Header);
        var rows = new List<Row>(t.Rows.Count);
        foreach (var r in t.Rows)
        {
            if (!t.Time(r, out var time)) continue;
            double cpu = t.Num(r, "cpu_pct");
            if (!double.IsFinite(cpu)) continue;
            rows.Add(new Row(time, t.Text(r, "app"), t.Text(r, "soort"), cpu, Z(t.Num(r, "geheugen_gb")), Z(t.Num(r, "energie_w")),
                Z(t.Num(r, "gpu_pct")), Z(t.Num(r, "mac_energie_w"))));
        }
        return rows;
    }

    static double Z(double v) => double.IsFinite(v) ? v : 0;
}
