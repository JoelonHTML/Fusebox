using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fusebox.Core.Network;

public sealed record KnownDevice(string? Name, string? Custom, string? Ip, string? Net, DateTime First, DateTime Last);

/// <summary>
/// Remembered devices, keyed by MAC ("AA:BB:CC:DD:EE:FF"): %USERPROFILE%\Documents\Fusebox\network-devices.json (same file format as the Mac:
/// pretty, sorted keys, ISO-8601 UTC dates, atomic write). Only devices with a MAC are remembered. Local only, never exported.
/// </summary>
public sealed class DeviceStore
{
    public DateTime Created { get; private set; }
    public Dictionary<string, KnownDevice> Devices { get; } = [];
    readonly string? path;

    public DeviceStore(string? path = null, DateTime? created = null) { this.path = path; Created = created ?? DateTime.UtcNow; }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fusebox", "network-devices.json");

    public KnownDevice? Get(string mac) => Devices.TryGetValue(mac, out var k) ? k : null;

    /// <summary>"New" = first seen today and more than 60 s after the store was created (so the very first scan doesn't make everything new).</summary>
    public bool IsNew(KnownDevice k, DateTime now) => k.First.ToLocalTime().Date == now.ToLocalTime().Date && k.First > Created.AddSeconds(60);

    /// <summary>After a completed scan: online devices with a MAC update last/ip/net/name; new MACs get first = now. Returns the devices that are new to the store (not this PC).</summary>
    public List<NetDevice> Remember(IEnumerable<NetDevice> online, string net, DateTime now)
    {
        var fresh = new List<NetDevice>();
        foreach (var d in online)
        {
            if (!d.Online || d.Mac == null) continue;
            var key = d.MacText;
            var k = Get(key) ?? new KnownDevice(null, null, null, null, now, now);
            if (Get(key) == null && !d.IsMe) fresh.Add(d);
            var best = d.Names.FirstOrDefault(n => n.Source is not ("Your name" or "Remembered" or "This PC"));
            Devices[key] = k with { Name = best.Name ?? k.Name, Last = now, Ip = d.IpText, Net = net };
        }
        Save();
        return fresh;
    }

    /// <summary>Own name for a device ("" clears it). Trimmed, max 64 characters.</summary>
    public void Rename(string mac, string name, DateTime now)
    {
        var clean = name.Trim();
        if (clean.Length > 64) clean = clean[..64];
        var k = Get(mac) ?? new KnownDevice(null, null, null, null, now, now);
        Devices[mac] = k with { Custom = clean.Length == 0 ? null : clean };
        Save();
    }

    public void Forget(string mac) { if (Devices.Remove(mac)) Save(); }

    // ---- file ----

    static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    static DateTime? Parse(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public string ToJson()
    {
        var devs = new JsonObject();
        foreach (var (mac, k) in Devices.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var o = new JsonObject();   // keys sorted
            if (k.Custom != null) o["custom"] = k.Custom;
            o["first"] = Iso(k.First);
            if (k.Ip != null) o["ip"] = k.Ip;
            o["last"] = Iso(k.Last);
            if (k.Name != null) o["name"] = k.Name;
            if (k.Net != null) o["net"] = k.Net;
            devs[mac] = o;
        }
        return new JsonObject { ["created"] = Iso(Created), ["devices"] = devs }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Tolerant: a broken file or entry gives an empty store / skips the entry.</summary>
    public static DeviceStore FromJson(string json, string? path = null)
    {
        var s = new DeviceStore(path);
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return s;
            if (Parse(root["created"]?.GetValue<string>()) is { } c) s.Created = c;
            if (root["devices"] is JsonObject devs)
                foreach (var (mac, v) in devs)
                {
                    if (NetAddress.ParseMac(mac) == null || v is not JsonObject o || Parse(o["first"]?.GetValue<string>()) is not { } first) continue;
                    string? Str(string n) => o[n] is JsonValue jv && jv.TryGetValue<string>(out var t) ? t : null;
                    s.Devices[mac.ToUpperInvariant()] = new KnownDevice(Str("name"), Str("custom"), Str("ip"), Str("net"), first, Parse(Str("last")) ?? first);
                }
        }
        catch { }
        return s;
    }

    public static DeviceStore Load(string? path = null)
    {
        path ??= DefaultPath;
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path), path) : new DeviceStore(path); }
        catch { return new DeviceStore(path); }
    }

    void Save()
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson());
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* read-only or full disk: keep it in memory */ }
    }
}
