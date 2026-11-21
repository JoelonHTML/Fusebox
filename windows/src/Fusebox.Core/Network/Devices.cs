using System.Globalization;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Network;

public enum DeviceKind { Phone, Computer, Tv, Speaker, Printer, Router, Nas, SmartHome, Console, Unknown }

/// <summary>Vendor lookup (IEEE list in oui.tsv: "AABBCC&lt;TAB&gt;Maker"), device-type guess and port helpers (ported from NetScanner/NetProbe).</summary>
public static class Devices
{
    static Dictionary<uint, string>? table;

    /// <summary>True once the maker list is loaded (never triggers the load: UI code asks this first).</summary>
    public static bool TableReady => table != null;

    /// <summary>Lazy load of the embedded oui.tsv (~40k lines, tens of ms; call from a background thread).</summary>
    public static Dictionary<uint, string> Table
    {
        get
        {
            if (table != null) return table;
            using var s = typeof(Devices).Assembly.GetManifestResourceStream("oui.tsv");
            if (s == null) return table = new();
            using var r = new StreamReader(s);
            return table = ParseOui(r.ReadToEnd());
        }
    }

    /// <summary>Maker for the first 3 bytes of a MAC; null = unknown.</summary>
    public static string? Vendor(byte[] mac, Dictionary<uint, string>? t = null)
    {
        if (mac.Length < 3) return null;
        uint key = (uint)mac[0] << 16 | (uint)mac[1] << 8 | mac[2];
        return (t ?? Table).TryGetValue(key, out var v) ? v : null;
    }

    public static Dictionary<uint, string> ParseOui(string text)
    {
        var t = new Dictionary<uint, string>(42_000);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.IndexOf('\t') != 6 || !uint.TryParse(line.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) continue;
            if (TidyVendor(line[7..]) is { } name) t[v] = name;
        }
        return t;
    }

    /// <summary>"NETGEAR" → "Netgear", "HUAWEI TECHNOLOGIES" → "Huawei Technologies"; registration blocks → null.</summary>
    public static string? TidyVendor(string s)
    {
        var v = s.Trim();
        if (v.Length == 0 || v is "IEEE Registration Authority" or "Private") return null;
        var words = v.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
            w.Length >= 4 && w.All(c => char.IsLetter(c) && char.IsUpper(c)) ? w[..1] + w[1..].ToLowerInvariant() : w);
        var r = string.Join(" ", words);
        return r.Length > 40 ? r[..40] : r;
    }

    /// <summary>Guess the device type from open ports, names and maker. ponytail: plain keywords, order decides; no Bonjour here so ports stand in for services.</summary>
    public static DeviceKind Guess(string name, string vendor, ICollection<ushort> ports, bool isRouter, bool isMe, bool privateMac, ICollection<string>? services = null)
    {
        services ??= [];
        bool Offers(params string[] t) => t.Any(services.Contains);
        if (isRouter) return DeviceKind.Router;
        if (isMe) return DeviceKind.Computer;
        string n = name.ToLowerInvariant(), v = vendor.ToLowerInvariant();
        bool Has(params string[] w) => w.Any(n.Contains);
        bool Open(params ushort[] p) => p.Any(ports.Contains);
        bool Made(params string[] p) => p.Any(v.StartsWith);
        if (Open(631, 9100) || Offers("_ipp._tcp", "_printer._tcp", "_scanner._tcp") || Made("hp", "hewlett packard", "canon", "epson", "seiko epson", "brother", "xerox", "lexmark", "kyocera")) return DeviceKind.Printer;
        if (Has("xbox", "playstation", "nintendo") || Made("nintendo")) return DeviceKind.Console;
        if (Has("iphone", "ipad", "galaxy", "pixel", "android", "phone")) return DeviceKind.Phone;
        if (Has("mac", "laptop", "desktop", "thinkpad", "-pc", "windows")) return DeviceKind.Computer;
        if (Has("homepod", "audioaccessory", "sonos", "speaker", "echo", "nest mini", "home mini", "soundbar", "zoneplayer") || Made("sonos", "bose")) return DeviceKind.Speaker;
        if (Has("appletv", "apple tv", "tv", "chromecast", "roku", "bravia", "fire", "mediarenderer") || Open(8009) || Offers("_googlecast._tcp") || Made("roku")) return DeviceKind.Tv;
        if (Offers("_sonos._tcp", "_spotify-connect._tcp", "_raop._tcp")) return DeviceKind.Speaker;
        if (Has("internetgatewaydevice", "router", "access point")) return DeviceKind.Router;
        if (Offers("_hap._tcp", "_hap._udp", "_homekit._tcp", "_matter._tcp", "_meshcop._udp") || Has("hue", "plug", "bulb", "thermostat", "camera", "doorbell", "esp", "bridge")
            || Made("signify", "philips lighting", "espressif", "ring", "google nest", "nest labs", "tado", "shelly", "tuya", "ikea", "lumi")) return DeviceKind.SmartHome;
        if (Has("nas", "diskstation", "synology", "qnap") || Made("synology", "qnap") || Open(5000, 5001)) return DeviceKind.Nas;
        if (Open(445, 22) || Offers("_rfb._tcp", "_workstation._tcp", "_ssh._tcp", "_sftp-ssh._tcp", "_afpovertcp._tcp", "_smb._tcp") || Made("intel", "asus", "microsoft", "raspberry pi", "dell", "lenovo", "hon hai", "liteon", "azurewave")) return DeviceKind.Computer;
        if (Made("tp-link", "ubiquiti", "netgear", "avm", "eero", "zyxel", "sagemcom", "arcadyan", "technicolor")) return DeviceKind.Router;
        if (privateMac) return DeviceKind.Phone;
        return DeviceKind.Unknown;
    }

    public static string KindTitle(DeviceKind k) => k switch
    {
        DeviceKind.Phone => L("Phone or tablet"), DeviceKind.Computer => L("Computer"), DeviceKind.Tv => L("TV / streaming"),
        DeviceKind.Speaker => L("Speaker"), DeviceKind.Printer => L("Printer"), DeviceKind.Router => L("Router / Wi-Fi"),
        DeviceKind.Nas => L("Storage (NAS)"), DeviceKind.SmartHome => L("Smart home"), DeviceKind.Console => L("Game console"),
        _ => L("Unknown"),
    };

    /// <summary>"Check ports": the 15 common ports (Mac NetProbe.ports), 0.3 s each.</summary>
    public static readonly ushort[] Ports = [22, 80, 443, 445, 548, 554, 631, 1883, 3389, 5000, 5001, 8080, 9100, 32400, 62078];

    /// <summary>Ports that answer for a device that ignores ping: their connect time stands in for ms.</summary>
    public static readonly ushort[] LatencyPorts = [62078, 80, 443, 7000];

    public static string PortName(ushort p) => p switch
    {
        22 => "SSH", 80 => "Web", 443 => L("Web (secure)"), 445 => L("File sharing"), 548 => L("File sharing (AFP)"), 554 => L("Camera stream"),
        631 => L("Printing"), 1883 => "MQTT", 3389 => L("Remote desktop"), 5000 => L("NAS / web"), 5001 => L("NAS (secure)"), 8080 => L("Web (alt)"),
        9100 => L("Printer"), 32400 => "Plex", 62078 => L("iPhone sync"), _ => L("Port %@", p),
    };

    /// <summary>Clickable web page for an open port, else null.</summary>
    public static string? WebUrl(uint ip, ushort port)
    {
        var h = NetAddress.IpString(ip);
        return port switch
        {
            80 => $"http://{h}/", 443 => $"https://{h}/", 631 or 5000 or 8080 => $"http://{h}:{port}/", 5001 => $"https://{h}:5001/", 32400 => $"http://{h}:32400/web", _ => null,
        };
    }

    /// <summary>Name when nothing was found: "Router", "Unknown device" or "Sonos speaker".</summary>
    public static string FallbackName(string? vendor, DeviceKind kind, bool isRouter, bool privateMac)
    {
        if (isRouter) return L("Router");
        if (vendor == null || privateMac) return L("Unknown device");
        var shortName = vendor.Length <= 16 ? vendor : vendor.Split(' ')[0];
        return shortName + " " + kind switch
        {
            DeviceKind.Phone => L("phone"), DeviceKind.Computer => L("computer"), DeviceKind.Tv => "TV", DeviceKind.Speaker => L("speaker"),
            DeviceKind.Printer => L("printer"), DeviceKind.Router => L("router"), DeviceKind.Nas => "NAS", DeviceKind.SmartHome => L("smart device"),
            DeviceKind.Console => L("console"), _ => L("device"),
        };
    }

    /// <summary>Search box: matches name, maker, IP part or MAC, case-insensitive; empty matches everything.</summary>
    public static bool Matches(string query, string name, string vendor, string ip, string mac)
    {
        var q = query.Trim();
        return q.Length == 0 || new[] { name, vendor, ip, mac }.Any(f => f.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"alex-iphone.fritz.box" → "alex-iphone"; bare IPs are no name.</summary>
    public static string? CleanHostname(string? s)
    {
        if (string.IsNullOrEmpty(s) || NetAddress.ParseIp(s) != null) return null;
        var first = s.Split('.')[0];
        return first.Length == 0 ? null : first;
    }
}
