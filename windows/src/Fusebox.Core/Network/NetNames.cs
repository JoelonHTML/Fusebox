using static Fusebox.Core.Strings;

namespace Fusebox.Core.Network;

/// <summary>Names, services and models of LAN devices (Mac NetScanner/NetProbe helpers). Everything that came over the network is untrusted: strict length limits.</summary>
public static class NetNames
{
    /// <summary>Order = priority of the Bonjour name (Apple device name > AirPlay > … > http) and the 22 service types we ask for.</summary>
    public static readonly string[] ServiceTypes =
    [
        "_device-info._tcp", "_companion-link._tcp", "_airplay._tcp", "_raop._tcp", "_googlecast._tcp", "_sonos._tcp", "_spotify-connect._tcp",
        "_ipp._tcp", "_printer._tcp", "_scanner._tcp", "_smb._tcp", "_afpovertcp._tcp", "_workstation._tcp", "_ssh._tcp", "_sftp-ssh._tcp", "_rfb._tcp",
        "_hap._tcp", "_hap._udp", "_homekit._tcp", "_http._tcp", "_meshcop._udp", "_matter._tcp",
    ];

    public static int Rank(string type) { int i = Array.IndexOf(ServiceTypes, type); return i < 0 ? 99 : i; }

    public static string ServiceTitle(string type) => type switch
    {
        "_companion-link._tcp" or "_device-info._tcp" => L("Apple device"), "_airplay._tcp" => "AirPlay", "_raop._tcp" => L("AirPlay audio"),
        "_googlecast._tcp" => "Chromecast", "_sonos._tcp" => "Sonos", "_spotify-connect._tcp" => "Spotify Connect",
        "_ipp._tcp" or "_printer._tcp" => L("Printing"), "_scanner._tcp" => L("Scanning"), "_smb._tcp" or "_afpovertcp._tcp" => L("File sharing"),
        "_workstation._tcp" => L("Computer"), "_ssh._tcp" or "_sftp-ssh._tcp" => L("Remote login (SSH)"), "_rfb._tcp" => L("Screen sharing"),
        "_hap._tcp" or "_hap._udp" or "_homekit._tcp" => "HomeKit", "_http._tcp" => L("Web page"), "_matter._tcp" => "Matter",
        "_meshcop._udp" => L("Thread border router"), _ => type,
    };

    /// <summary>Service instance name → device name (Mac friendlyName): RAOP/Sonos "MAC@Name" → "Name", workstation "host [aa:bb:…]" → "host", Cast uses TXT fn, Matter ignored.</summary>
    public static string? FriendlyName(string raw, string type, IReadOnlyDictionary<string, string> txt)
    {
        if (type == "_matter._tcp") return null;
        if (type == "_googlecast._tcp" && txt.TryGetValue("fn", out var fn) && fn.Length > 0) return Cap(fn);
        var name = raw;
        if (type is "_raop._tcp" or "_sonos._tcp") name = raw.Split('@', 2)[^1];
        if (type == "_workstation._tcp" && name.IndexOf('[') is var b and >= 0) name = name[..b].Trim();
        name = name.Trim();
        return name.Length == 0 ? null : Cap(name);
    }

    public static string Cap(string s, int n = 64) => s.Length <= n ? s : s[..n];

    /// <summary>"MacBookPro18,3" → "MacBook Pro"; null when it isn't an Apple identifier.</summary>
    public static string? AppleModel(string id)
    {
        (string Prefix, string Name)[] map =
        [
            ("MacBookPro", "MacBook Pro"), ("MacBookAir", "MacBook Air"), ("MacBook", "MacBook"), ("iMac", "iMac"), ("Macmini", "Mac mini"), ("MacPro", "Mac Pro"),
            ("Mac", "Mac"), ("iPhone", "iPhone"), ("iPad", "iPad"), ("iPod", "iPod touch"), ("AppleTV", "Apple TV"), ("AudioAccessory5,", "HomePod mini"),
            ("AudioAccessory", "HomePod"), ("Watch", "Apple Watch"), ("RealityDevice", "Apple Vision Pro"),
        ];
        foreach (var (prefix, name) in map)
            if (id.StartsWith(prefix, StringComparison.Ordinal) && id.Length > prefix.Length && char.IsDigit(id[prefix.Length])) return name;
        return null;
    }

    /// <summary>"MacBook Pro (MacBookPro18,3) · Sonos One by Sonos, Inc."</summary>
    public static string? ModelText(string? bonjourModel, UpnpInfo? up)
    {
        var parts = new List<string>();
        if (bonjourModel != null) parts.Add(AppleModel(bonjourModel) is { } a ? $"{a} ({bonjourModel})" : bonjourModel);
        if (up?.ModelName is { } m && !parts.Any(p => p.Contains(m, StringComparison.OrdinalIgnoreCase)))
            parts.Add(m + (up.Manufacturer != null ? L(" by %@", up.Manufacturer) : ""));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Source of a name as shown; the source id itself stays English (compared in the store).</summary>
    public static string SourceTitle(string s) => s switch { "Remembered" => L("Remembered"), "This PC" => L("This PC"), "Your name" => L("Your name"), _ => s };

    /// <summary>Model key in a Bonjour TXT record (device-info wins over the others).</summary>
    public static string? TxtModel(IReadOnlyDictionary<string, string> txt)
    {
        foreach (var k in new[] { "model", "am", "rpMd", "md", "ty", "usb_MDL" })
            if (txt.TryGetValue(k, out var v) && v.Length is > 0 and < 64) return v;
        return null;
    }

    /// <summary>Add one resolved service to a host (Mac add()): the earliest service type in <see cref="ServiceTypes"/> names the device.</summary>
    public static void AddService(BonjourHost b, string type, string? name, string? model)
    {
        bool better = b.Types.Where(t => t != type).All(t => Rank(t) > Rank(type));
        b.Types.Add(type);
        if (name != null && (b.Name == null || better)) b.Name = Cap(name);
        if (model != null && (b.Model == null || type == "_device-info._tcp")) b.Model = model;
    }
}
