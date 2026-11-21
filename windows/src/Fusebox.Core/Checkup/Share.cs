using System.Text;
using System.Text.RegularExpressions;

namespace Fusebox.Core.Checkup;

/// <summary>Who this PC belongs to, so the report can leave it out (§11.6, PLAN §7.4 item 9).</summary>
public sealed record ScrubIdentity(string UserName, string FullName, string ComputerName, string Domain);

/// <summary>
/// The privacy scrubber every shared report passes through: user profile paths → "~", user name, full name, computer name and domain
/// (≥ 4 characters, not a generic word) → "…", IPv4 → x.x.x.x, MAC addresses → xx:xx:xx:xx:xx:xx.
/// </summary>
public static partial class Scrubber
{
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    { "pc", "laptop", "desktop", "windows", "user", "users", "admin", "administrator", "home", "local", "computer", "workstation", "owner", "default", "workgroup", "wsl" };

    [GeneratedRegex(@"[A-Za-z]:[\\/]Users[\\/][^\\/\s:*?""<>|]+", RegexOptions.IgnoreCase)] private static partial Regex ProfilePath();
    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b")] private static partial Regex Ipv4();
    [GeneratedRegex(@"\b[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}\b")] private static partial Regex Mac();

    public static string Scrub(string text, ScrubIdentity id)
    {
        text = ProfilePath().Replace(text, "~");
        text = Mac().Replace(text, "xx:xx:xx:xx:xx:xx");
        text = Ipv4().Replace(text, "x.x.x.x");
        foreach (var name in new[] { id.FullName, id.UserName, id.ComputerName, id.Domain }
                     .Select(n => n?.Trim() ?? "").Where(n => n.Length >= 4 && !Generic.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length))
            text = Regex.Replace(text, Regex.Escape(name), "…", RegexOptions.IgnoreCase);
        return text;
    }
}

public sealed class ReportInfo
{
    public string Processor = "", Windows = "";
    public double MemoryGb = double.NaN, DiskTotalGb = double.NaN, DiskFreeGb = double.NaN;
    public int UptimeMinutes = -1;
    public DateTime? Boot;
    public string? Battery;
    public double? StabilityIndex;
}

/// <summary>The plain-text report (§5.9) and the merged CSV export. English on purpose: it is meant to be pasted into a support request.</summary>
public static class ReportText
{
    public static string Build(Report r, ReportInfo i, IReadOnlyList<CheckupTip> tips, DateTime now)
    {
        var c = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var sb = new StringBuilder();
        sb.Append("Fusebox Checkup report — ").AppendLine(PeriodName(r.Period));
        sb.AppendLine("Created " + now.ToString("ddd d MMM yyyy HH:mm", c));
        sb.AppendLine().AppendLine("PC");
        if (i.Processor.Length > 0) sb.AppendLine("Processor: " + i.Processor);
        if (double.IsFinite(i.MemoryGb)) sb.AppendLine($"Memory: {Math.Round(i.MemoryGb)} GB");
        if (i.Windows.Length > 0) sb.AppendLine("Windows: " + i.Windows);
        if (i.UptimeMinutes >= 0) sb.AppendLine("On for: " + Fmt.Duration(i.UptimeMinutes) + (i.Boot is { } b ? " (since " + b.ToString("ddd d MMM HH:mm", c) + ")" : ""));
        if (double.IsFinite(i.DiskTotalGb)) sb.AppendLine($"Disk: {Math.Round(i.DiskTotalGb)} GB, {Math.Round(i.DiskFreeGb)} GB free");
        if (i.StabilityIndex is { } si) sb.AppendLine($"Windows stability index: {Fmt.One(si)} of 10");
        if (i.Battery != null) sb.AppendLine().AppendLine("BATTERY").AppendLine(i.Battery);

        sb.AppendLine().AppendLine($"HEALTH ({PeriodName(r.Period).ToLowerInvariant()})");
        sb.AppendLine($"Score: {(r.Score?.ToString() ?? "–")}/10 — {r.Reason}");
        if (r.Minutes > 0)
            sb.AppendLine($"Measured: {Fmt.Duration(r.Minutes)} / Busy (CPU ≥ 85%): {Fmt.Duration(r.BusyMin)} / Memory (almost) full: {Fmt.Duration(r.PressureMin)} / Hot (≥ 95 °C): {Fmt.Duration(r.HotMin)}");

        if (r.Busy.Count > 0)
        {
            sb.AppendLine().AppendLine("BUSY MOMENTS");
            foreach (var b in r.Busy) sb.AppendLine($"- {CheckupFmt.Span(b.Start, b.End)} · {Fmt.Pct(b.Cpu)}" + (b.App != null ? " · mostly " + b.App : ""));
        }
        if (r.TopCpu.Count + r.TopRam.Count > 0)
        {
            sb.AppendLine().AppendLine("TOP APPS");
            if (r.TopCpu.Count > 0) sb.AppendLine("By CPU: " + string.Join(", ", r.TopCpu.Select(a => $"{a.Name} {Fmt.Pct(a.Cpu)}")));
            if (r.TopRam.Count > 0) sb.AppendLine("By memory: " + string.Join(", ", r.TopRam.Select(a => $"{a.Name} {Fmt.One(a.MemGb)} GB")));
        }
        if (r.Leaks.Count + r.Runaways.Count > 0)
        {
            sb.AppendLine().AppendLine("FINDINGS");
            foreach (var l in r.Leaks) sb.AppendLine($"- {l.App} grew from {Fmt.One(l.From)} to {Fmt.One(l.To)} GB in {l.Hours} h");
            foreach (var x in r.Runaways) sb.AppendLine($"- {x.App} used a full processor core for {CheckupFmt.Mins(x.Minutes)} ({CheckupFmt.Span(x.Start, x.End)})");
        }
        var k = r.Crashes;
        if (k.Any)
        {
            sb.AppendLine().AppendLine("CRASHES AND REPORTS FROM WINDOWS");
            foreach (var a in k.Apps)
                sb.AppendLine($"- {a.App}: " + string.Join(", ", new[] { a.Crashes > 0 ? $"crashed {a.Crashes}×" : "", a.Hangs > 0 ? $"stopped responding {a.Hangs}×" : "" }.Where(s => s.Length > 0)) + (a.Loop ? $", {a.LoopCrashes}× in one hour" : ""));
            if (k.OutOfMemory > 0) sb.AppendLine($"- Out of memory warnings: {k.OutOfMemory}×");
            if (k.BlueScreens > 0) sb.AppendLine($"- Blue screens: {k.BlueScreens}");
            if (k.UnexpectedShutdowns > 0) sb.AppendLine($"- Unexpected shutdowns: {k.UnexpectedShutdowns}");
        }
        if (r.Jobs.Count > 0)
        {
            sb.AppendLine().AppendLine("WINDOWS HOUSEKEEPING SEEN");
            foreach (var j in r.Jobs) sb.AppendLine($"- {j.Job}: {Fmt.Duration(j.Minutes)}");
        }
        if (tips.Count(t => t.Id != "login") > 0)
        {
            sb.AppendLine().AppendLine("TIPS");
            foreach (var t in tips.Where(t => t.Id != "login")) sb.AppendLine("- " + t.Text.Replace('\n', ' '));
        }
        return sb.ToString();
    }

    static string PeriodName(Period p) => p switch { Period.Today => "Today", Period.Yesterday => "Yesterday", _ => "7 days" };

    /// <summary>All main logs of the period in the current column order (missing columns empty), one header. Texts oldest day first.</summary>
    public static string MergeCsv(IEnumerable<string> dayTexts)
    {
        var sb = new StringBuilder(CsvLog.Header);
        foreach (var text in dayTexts)
        {
            var body = CsvLog.Remap(text, CsvLog.Header) ?? text;
            var lines = body.Split('\n').Skip(1).Where(l => l.Trim().Length > 0);
            foreach (var l in lines) sb.Append(l.TrimEnd('\r')).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>For the tray flyout (§5.10): score, reason and one tip.</summary>
public sealed record CheckupGist(int? Score, string Reason, string? TipHead, string? TipRest, bool TipSerious)
{
    /// <summary>The first serious tip, else the first one that is not the always-present "login" tip.</summary>
    public static CheckupGist From(Report r, IReadOnlyList<CheckupTip> tips)
    {
        var tip = tips.FirstOrDefault(t => t.Serious) ?? tips.FirstOrDefault(t => t.Id != "login");
        if (tip == null) return new(r.Score, r.Reason, null, null, false);
        var (head, rest) = tip.Split();
        return new(r.Score, r.Reason, head, rest, tip.Serious);
    }
}
