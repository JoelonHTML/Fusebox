using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Fusebox.Core.Checkup;

/// <summary>Parsers for everything the Windows readers hand back as text: event XML, PowerShell JSON, powercfg output, StartupApproved bytes.</summary>
public static class CheckupParsers
{
    // ---- Event Log ----

    /// <summary>Parses one EvtRender(EvtRenderEventXml) result. Null when it isn't an event.</summary>
    public static EventInfo? ParseEvent(string xml)
    {
        try
        {
            var root = XDocument.Parse(xml).Root;
            if (root == null) return null;
            XNamespace ns = root.Name.Namespace;
            var sys = root.Element(ns + "System");
            if (sys == null) return null;
            int id = int.Parse(sys.Element(ns + "EventID")?.Value ?? "0", CultureInfo.InvariantCulture);
            var provider = sys.Element(ns + "Provider")?.Attribute("Name")?.Value ?? "";
            var stamp = sys.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            var time = stamp != null && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                ? t.LocalDateTime : DateTime.MinValue;
            var data = (root.Element(ns + "EventData")?.Elements(ns + "Data") ?? [])
                .Select(d => ((string?)d.Attribute("Name"), d.Value)).ToList();
            return new(time, provider, id, data);
        }
        catch (Exception e) when (e is System.Xml.XmlException or FormatException) { return null; }
    }

    /// <summary>Kernel-Power 42 = entering sleep, Power-Troubleshooter 1 = woke up (with the wake source).</summary>
    public static SleepSummary SummarizeSleep(IEnumerable<EventInfo> events)
    {
        int sleeps = 0;
        var wakes = new List<WakeEvent>();
        foreach (var e in events)
        {
            if (e.Provider == "Microsoft-Windows-Kernel-Power" && e.Id == 42) sleeps++;
            else if (e.Provider == "Microsoft-Windows-Power-Troubleshooter" && e.Id == 1)
            {
                var wake = e.Get("WakeTime") is { } w && DateTime.TryParse(w, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var wt) ? wt.ToLocalTime() : e.Time;
                var src = e.Data.FirstOrDefault(d => d.Name?.StartsWith("WakeSource", StringComparison.OrdinalIgnoreCase) == true && d.Name.EndsWith("Text", StringComparison.OrdinalIgnoreCase)).Value
                          ?? e.Data.FirstOrDefault(d => d.Name?.StartsWith("WakeSource", StringComparison.OrdinalIgnoreCase) == true).Value;
                wakes.Add(new(wake, string.IsNullOrWhiteSpace(src) ? "?" : src.Trim()));
            }
        }
        return new(wakes.OrderByDescending(w => w.Time).ToList(), sleeps);
    }

    // ---- powercfg /requests ----

    static readonly string[] Sections = ["DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION", "PERFBOOST", "ACTIVELOCKSCREEN"];

    public static List<WakeRequest> ParsePowercfgRequests(string text)
    {
        var res = new List<WakeRequest>();
        string? section = null, who = null; var reason = new List<string>();
        void Flush() { if (section != null && who != null) res.Add(new(section, who, string.Join(" ", reason))); who = null; reason.Clear(); }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.EndsWith(':') && Sections.Contains(line[..^1])) { Flush(); section = line[..^1]; continue; }
            if (section == null || line.Length == 0) { if (line.Length == 0) Flush(); continue; }
            if (line.StartsWith("None", StringComparison.OrdinalIgnoreCase) && who == null) continue;
            if (line.StartsWith('[')) { Flush(); who = line; }
            else if (who != null) reason.Add(line);
        }
        Flush();
        return res;
    }

    // ---- PowerShell JSON ----

    static IEnumerable<JsonObject> Objects(JsonNode? n) =>
        n switch { JsonArray a => a.OfType<JsonObject>(), JsonObject o => [o], _ => [] };

    static double? Num(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, CultureInfo.InvariantCulture, out d)) return d;
        }
        return null;
    }

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "";

    /// <summary>Win32_ReliabilityStabilityMetrics: first object's SystemStabilityIndex (1–10).</summary>
    public static double? ParseStabilityIndex(string json)
    {
        try { return Objects(JsonNode.Parse(json)).Select(o => Num(o["SystemStabilityIndex"])).FirstOrDefault(v => v != null); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Get-PhysicalDisk: works without admin. MediaType/HealthStatus come as text or as the CIM enum number.</summary>
    public static List<DiskInfo> ParseDisks(string json)
    {
        try
        {
            return Objects(JsonNode.Parse(json)).Select(o =>
            {
                var media = Str(o["MediaType"]);
                media = media switch { "3" => "HDD", "4" => "SSD", "5" => "SCM", "0" => "", _ => media };
                var health = Str(o["HealthStatus"]);
                health = health switch { "0" => "Healthy", "1" => "Warning", "2" => "Unhealthy", _ => health };
                return new DiskInfo(Str(o["FriendlyName"]), media, health, (long)(Num(o["Size"]) ?? 0));
            }).ToList();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>Output of the elevated "ssd" task: {"disks":[…],"rel":[…]}.</summary>
    public static (List<DiskInfo> Disks, List<DiskReliability> Counters, string? Error) ParseSsd(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null) return ([], [], "no data");
            if (root["error"] != null) return ([], [], Str(root["error"]));
            var disks = ParseDisks(root["disks"]?.ToJsonString() ?? "[]");
            var rel = Objects(root["rel"]).Select(o => new DiskReliability(Str(o["DeviceId"]),
                Num(o["Wear"]) is { } w ? (int)w : null, Num(o["Temperature"]), Num(o["ReadErrorsTotal"]) is { } r ? (long)r : null,
                Num(o["PowerOnHours"]) is { } p ? (long)p : null)).ToList();
            return (disks, rel, null);
        }
        catch (System.Text.Json.JsonException) { return ([], [], "no data"); }
    }

    /// <summary>{"Full":…,"Design":…,"Cycles":…} → health = full / design.</summary>
    public static BatteryHealth? ParseBattery(string json)
    {
        try
        {
            var o = Objects(JsonNode.Parse(json)).FirstOrDefault();
            if (o == null) return null;
            double? full = Num(o["Full"]), design = Num(o["Design"]), cycles = Num(o["Cycles"]);
            int? pct = full > 0 && design > 0 ? (int)Math.Min(100, Math.Round(full.Value * 100 / design.Value)) : null;
            return new(pct, cycles > 0 ? (int)cycles : null);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // ---- StartupApproved ----

    /// <summary>First byte 0x02/0x06 = enabled, 0x03/0x07 = disabled; no value = enabled.</summary>
    public static bool StartupEnabled(byte[]? value) => value is not { Length: > 0 } || (value[0] & 1) == 0;

    /// <summary>The 12 bytes Task Manager writes: flag + FILETIME of the change (disabled only).</summary>
    public static byte[] StartupApprovedValue(bool enabled, DateTime utcNow)
    {
        var b = new byte[12];
        b[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.TryWriteBytes(b.AsSpan(4), utcNow.ToFileTimeUtc());
        return b;
    }
}
