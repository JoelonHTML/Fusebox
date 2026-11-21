using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Fusebox.Core.Processes;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Checkup;

/// <summary>Battery health over time (§5.B.5.4): battery-health.csv, filled up from Windows' own battery report on day one.</summary>
public static class BatteryTrend
{
    public sealed record Point(DateTime Day, double Pct, int? Cycles);

    /// <summary>battery-health.csv ("datum,gezondheid_pct,cycli"), oldest first, one point per day.</summary>
    public static List<Point> ParseCsv(string csv)
    {
        var t = CsvTable.Parse(csv);
        var list = new List<Point>();
        foreach (var r in t.Rows)
        {
            if (!DateTime.TryParseExact(r[0], "yyyy-MM-dd", CsvLog.Culture, DateTimeStyles.None, out var d)) continue;
            double pct = t.Num(r, "gezondheid_pct"), cyc = t.Num(r, "cycli");
            if (double.IsFinite(pct)) list.Add(new(d, pct, double.IsFinite(cyc) ? (int)cyc : null));
        }
        return list.GroupBy(p => p.Day).Select(g => g.Last()).OrderBy(p => p.Day).ToList();
    }

    /// <summary>
    /// <c>powercfg /batteryreport /xml</c> (namespace http://schemas.microsoft.com/battery/2012, no admin needed): present capacities and
    /// the capacity history. Tolerant on purpose: every HistoryEntry that carries a date plus design and full-charge capacity counts, whether the
    /// values are attributes or child elements. The battery's serial number and id are never read.
    /// </summary>
    public static (BatteryHealth? Now, List<Point> History) ParseReport(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            static string? Val(XElement e, string name) =>
                e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value
                ?? e.Elements().FirstOrDefault(c => c.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
            static double? Num(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;

            BatteryHealth? now = null;
            var bat = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Battery" && Num(Val(e, "DesignCapacity")) != null);
            if (bat != null && Num(Val(bat, "DesignCapacity")) is { } design && Num(Val(bat, "FullChargeCapacity")) is { } full)
                now = new((int)Math.Min(100, Math.Round(full * 100 / design)), Num(Val(bat, "CycleCount")) is { } c ? (int)c : null);

            var points = new List<Point>();
            foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "HistoryEntry"))
            {
                var date = Val(e, "LocalStartDate") ?? Val(e, "StartDate") ?? Val(e, "Start") ?? Val(e, "Timestamp");
                if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day)) continue;
                if (Num(Val(e, "DesignCapacity")) is { } d && Num(Val(e, "FullChargeCapacity")) is { } f)
                    points.Add(new(day.Date, Math.Min(100, Math.Round(f * 100 / d, 1)), Num(Val(e, "CycleCount")) is { } cy ? (int)cy : null));
            }
            return (now, points.GroupBy(p => p.Day).Select(g => g.Last()).OrderBy(p => p.Day).ToList());
        }
        catch (System.Xml.XmlException) { return (null, []); }
    }

    /// <summary>The CSV points, or — while the CSV has fewer than 2 days — Windows' own history.</summary>
    public static List<Point> Merge(List<Point> csv, List<Point> windows)
    {
        if (csv.Count >= 2) return csv;
        return windows.Concat(csv).GroupBy(p => p.Day).Select(g => g.Last()).OrderBy(p => p.Day).ToList();
    }

    /// <summary>"Since 2026-10-01: 95% → 92% · +41 cycles" (cycles only when both ends know them).</summary>
    public static string Summary(IReadOnlyList<Point> p)
    {
        if (p.Count < 2) return "";
        var a = p[0]; var b = p[^1];
        var s = L("Since %@: %@ → %@", a.Day.ToString("yyyy-MM-dd", CsvLog.Culture), Fmt.Pct(a.Pct), Fmt.Pct(b.Pct));
        return a.Cycles is { } x && b.Cycles is { } y ? s + L(" · +%lld cycles", y - x) : s;
    }
}

/// <summary>Windows' own stability history (Reliability Monitor), for the 30-day sparkline.</summary>
public static class Reliability
{
    public sealed record Point(DateTime Time, double Index);

    /// <summary>JSON from <c>Win32_ReliabilityStabilityMetrics | select T, I</c> where T is an ISO-8601 string and I the index (1–10).</summary>
    public static List<Point> Parse(string json)
    {
        var list = new List<Point>();
        try
        {
            var node = JsonNode.Parse(json);
            foreach (var o in node is JsonArray a ? a.OfType<JsonObject>() : node is JsonObject one ? [one] : [])
            {
                if (!DateTime.TryParse(o["T"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) continue;
                if (!double.TryParse(o["I"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var i) || i is < 1 or > 10) continue;
                list.Add(new(t.ToLocalTime(), i));
            }
        }
        catch (System.Text.Json.JsonException) { }
        return list.OrderBy(p => p.Time).ToList();
    }

    /// <summary>One value per day (the day's last), oldest first, at most 30.</summary>
    public static List<double> Daily(IEnumerable<Point> p) =>
        p.GroupBy(x => x.Time.Date).OrderBy(g => g.Key).TakeLast(30).Select(g => g.OrderBy(x => x.Time).Last().Index).ToList();
}

/// <summary>"What drained my battery" (§5.B.5.1): app shares from the apps log since the PC was unplugged.</summary>
public static class Drain
{
    public sealed record Share(string App, double Pct, double Weight);
    public sealed record Result(DateTime Start, DateTime? End, double? PctStart, double? PctEnd, List<Share> Shares, bool OnBattery);

    /// <summary>
    /// The last stretch on battery from the battery % column of the main log: it starts after the last time the % rose and runs to the end.
    /// Null when there is no battery data. <paramref name="onBattery"/> is the live state; when the PC is on the charger the window is today from midnight.
    /// </summary>
    public static (DateTime Start, DateTime? End, double? PctStart, double? PctEnd)? Window(IReadOnlyList<(DateTime T, double Pct)> samples, bool onBattery, DateTime now)
    {
        var s = samples.Where(x => double.IsFinite(x.Pct)).OrderBy(x => x.T).ToList();
        if (s.Count < 2) return null;
        int start = 0;
        for (int i = s.Count - 1; i > 0; i--) if (s[i].Pct > s[i - 1].Pct + 0.05) { start = i; break; }
        if (!onBattery) return (now.Date, null, null, null);   // "Today · on the charger now"
        return (s[start].T, null, s[start].Pct, s[^1].Pct);
    }

    /// <summary>Per-app share of CPU use in the window (top 6). Windows has no per-app watts: the share is of the CPU time apps used, and is labelled that way.</summary>
    public static List<Share> Rank(IEnumerable<AppsLog.Row> rows, DateTime from, DateTime to)
    {
        var by = rows.Where(r => r.Time >= from && r.Time <= to).GroupBy(r => r.App).Select(g => (App: g.Key, W: g.Sum(r => r.Cpu))).Where(x => x.W > 0).ToList();
        double total = by.Sum(x => x.W);
        return total <= 0 ? [] : by.OrderByDescending(x => x.W).Take(6).Select(x => new Share(x.App, x.W / total * 100, x.W)).ToList();
    }
}

/// <summary>"Battery use while asleep" (§5.B.5.2) from the Event Log (Power-Troubleshooter 1 carries sleep and wake time) and the battery % around it.</summary>
public static class SleepSpans
{
    public sealed record Span(DateTime Sleep, DateTime Wake, string Reason, double? Loss, bool Charger)
    {
        public int Minutes => (int)(Wake - Sleep).TotalMinutes;
        /// <summary>≥ 3 % and more than 1 % per hour.</summary>
        public bool Heavy => Loss is { } l && l >= 3 && l * 60 > Minutes;
    }

    /// <summary>Spans of ≥ 30 minutes, newest first. <paramref name="battery"/> = (time, %) samples of the main log, used within 5 minutes of sleep and wake.</summary>
    public static List<Span> Build(IEnumerable<EventInfo> events, IReadOnlyList<(DateTime T, double Pct)> battery, int max = 4)
    {
        var list = new List<Span>();
        foreach (var e in events.Where(e => e.Provider == "Microsoft-Windows-Power-Troubleshooter" && e.Id == 1))
        {
            if (!Utc(e.Get("SleepTime"), out var sleep) || !Utc(e.Get("WakeTime"), out var wake) || wake <= sleep) continue;
            if ((wake - sleep).TotalMinutes < 30) continue;
            var src = e.Data.FirstOrDefault(d => d.Name?.StartsWith("WakeSourceText", StringComparison.OrdinalIgnoreCase) == true).Value ?? e.Get("WakeSource") ?? "";
            double? before = Near(battery, sleep, before: true), after = Near(battery, wake, before: false);
            bool charger = before is { } b && after is { } a && a > b + 0.05;
            list.Add(new(sleep, wake, WakeReason(src), before is { } b2 && after is { } a2 && !charger ? Math.Max(0, Math.Round(b2 - a2)) : null, charger));
        }
        return list.OrderByDescending(s => s.Wake).Take(max).ToList();
    }

    static bool Utc(string? s, out DateTime t)
    {
        t = default;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u)) return false;
        t = u.ToLocalTime();
        return true;
    }

    static double? Near(IReadOnlyList<(DateTime T, double Pct)> s, DateTime t, bool before)
    {
        var c = before ? s.Where(x => x.T <= t && (t - x.T).TotalMinutes <= 5).OrderByDescending(x => x.T)
                       : s.Where(x => x.T >= t && (x.T - t).TotalMinutes <= 5).OrderBy(x => x.T);
        var f = c.FirstOrDefault();
        return f.T == default ? null : f.Pct;
    }

    /// <summary>Windows' wake source text in plain words (first rule that matches wins).</summary>
    public static string WakeReason(string source)
    {
        var s = source.ToLowerInvariant();
        if (s.Trim().Length == 0 || s == "?") return L("unknown");
        if (s.Contains("power button") || s.Contains("fixed feature")) return L("power button");
        if (s.Contains("lid")) return L("lid opened");
        if (s.Contains("keyboard")) return L("keyboard");
        if (s.Contains("mouse") || s.Contains("touch") || s.Contains("hid")) return L("keyboard or trackpad");
        if (s.Contains("bluetooth")) return L("Bluetooth");
        if (s.Contains("wi-fi") || s.Contains("wlan") || s.Contains("wireless") || s.Contains("network") || s.Contains("ethernet") || s.Contains("lan")) return L("network access (Wi-Fi/Bluetooth)");
        if (s.Contains("usb")) return L("USB device");
        if (s.Contains("timer") || s.Contains("rtc")) return L("timer");
        if (s.Contains("charger") || s.Contains("ac ") || s.Contains("adapter")) return L("charger connected");
        return L("other");
    }
}

/// <summary>"What keeps my PC awake" (§5.B.5.3): powercfg /requests in plain words.</summary>
public static class AwakeRequests
{
    public sealed record Hold(string App, string Text);

    public static List<Hold> From(IEnumerable<WakeRequest> requests)
    {
        var list = new List<Hold>();
        foreach (var r in requests)
        {
            var text = r.Section switch
            {
                "DISPLAY" => L("keeping the screen on"),
                "SYSTEM" or "EXECUTION" => L("preventing sleep"),
                "AWAYMODE" => L("preventing all sleep"),
                _ => null,
            };
            if (text == null) continue;
            var app = Who(r.Who, r.Reason);
            if (!list.Any(h => h.App == app && h.Text == text)) list.Add(new(app, text));
        }
        return list;
    }

    /// <summary>"[PROCESS] \Device\…\Zoom.exe" → "Zoom"; "[DRIVER] Realtek" with an audio reason → "Audio playing"; "[SERVICE] name" → name.</summary>
    public static string Who(string who, string reason)
    {
        int close = who.IndexOf(']');
        var kind = close > 0 ? who[1..close].ToUpperInvariant() : "";
        var name = close > 0 ? who[(close + 1)..].Trim() : who.Trim();
        if (reason.Contains("audio", StringComparison.OrdinalIgnoreCase)) return L("Audio playing");
        if (kind == "PROCESS") { name = name[(name.LastIndexOf('\\') + 1)..]; if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4]; }
        return name.Length == 0 ? "?" : name;
    }
}

/// <summary>SSD speed test result (§5.B.4), kept as JSON in pref ssdBenchLast.</summary>
public sealed record SsdBenchResult(double ReadMBps, double WriteMBps, DateTime When)
{
    /// <summary>Mac rule: read ≥ 1500 and write ≥ 1000 MB/s. On Windows a SATA SSD (≈ 500 MB/s) is expected too, so "slower than usual" starts below 400 / 300.</summary>
    public bool Fast => ReadMBps >= 1500 && WriteMBps >= 1000;
    public bool Slow => ReadMBps < 400 || WriteMBps < 300;

    public string ToJson() => new JsonObject { ["read"] = Math.Round(ReadMBps, 1), ["write"] = Math.Round(WriteMBps, 1), ["when"] = When.ToString("o", CultureInfo.InvariantCulture) }.ToJsonString();

    public static SsdBenchResult? Parse(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject o) return null;
            if (!double.TryParse(o["read"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) || !double.TryParse(o["write"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return null;
            return DateTime.TryParse(o["when"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) && r > 0 && w > 0 ? new(r, w, t) : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>1 GiB in MB per second.</summary>
    public static double Speed(double seconds) => seconds <= 0 ? 0 : 1_073_741_824 / 1e6 / seconds;
}
