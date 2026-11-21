using static Fusebox.Core.Strings;

namespace Fusebox.Core.Network;

/// <summary>Our own connection as the scanner needs it (no Windows types).</summary>
public sealed record ScanInfo(uint Ip, int Prefix, uint? Router, byte[]? Mac, string ComputerName)
{
    public uint Mask => NetAddress.MaskFromPrefix(Prefix);
    public bool IsLocal(uint a) => (a & Mask) == (Ip & Mask);
    /// <summary>"192.168.1.0/24 via 192.168.1.1": tells home and office apart in the remembered devices.</summary>
    public string NetworkKey => $"{NetAddress.IpString(Ip & Mask)}/{Prefix}" + (Router is { } r ? " via " + NetAddress.IpString(r) : "");
}

public sealed class FoundHost { public byte[]? Mac; public double? Ms; public double? Loss; public bool ViaTcp; }
public sealed class BonjourHost { public string? Name; public HashSet<string> Types = []; public string? Model; }
public sealed record UpnpInfo(string? FriendlyName, string? Manufacturer, string? ModelName, string? DeviceType);

/// <summary>One row of the device list (Mac NetDevice). Built from the scan facts + the remembered store; never mutated by the UI.</summary>
public sealed class NetDevice
{
    public string Id = "";
    public uint Ip;                       // 0 = unknown (offline device without a remembered address)
    public byte[]? Mac;
    public bool Online = true, IsMe, IsRouter, ViaTcp, Remote, PrivateMac, IsNew, CheckingPorts;
    public double? Ms, Loss;              // Loss 0..1 over 3 pings
    public string Name = "", Vendor = "–";   // "–" = unknown maker
    public string? Custom, Model;
    public List<(string Source, string Name)> Names = [];   // all names found, best first
    public HashSet<string> Services = [];
    public DeviceKind Kind = DeviceKind.Unknown;
    public ushort[]? Ports;
    public DateTime? FirstSeen, LastSeen;

    public string IpText => Ip == 0 ? "–" : NetAddress.IpString(Ip);
    public string MacText => Mac == null ? "–" : NetAddress.MacString(Mac);
    public string VendorTitle => PrivateMac ? L("Private address") : Vendor;
    public bool HasVendor => Vendor != "–" && !PrivateMac;

    /// <summary>Search box: any found name, current name, IP, maker, model; MAC only when <paramref name="withMac"/> (column not hidden).</summary>
    public bool Matches(string query, bool withMac)
    {
        var q = query.Trim();
        if (q.Length == 0) return true;
        bool Has(string? s) => s != null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        return Names.Any(n => Has(n.Name)) || Has(Name) || Has(Ip == 0 ? null : IpText) || Has(HasVendor ? Vendor : null) || Has(Model) || (withMac && Has(Mac == null ? null : MacText));
    }
}

/// <summary>Everything a scan found so far. The scanner changes it under <see cref="Gate"/>; <see cref="NetDevices.Build"/> reads it under the same lock.</summary>
public sealed class ScanState
{
    public readonly object Gate = new();
    public ScanInfo? Info;
    public HashSet<uint> Targets = [];
    public Dictionary<uint, FoundHost> Found = [];
    public Dictionary<uint, BonjourHost> Bonjour = [];
    public Dictionary<uint, Dictionary<string, string>> Names = [];   // ip → source (mDNS, NetBIOS, DNS) → name
    public Dictionary<uint, UpnpInfo> Upnp = [];
    public Dictionary<uint, ushort[]> Ports = [];
    public HashSet<uint> Checking = [];
    public bool ArpDone, Blocked, CustomRange, Scanning, IncludeMe = true;   // IncludeMe: false when a custom range does not contain our own address
}

public static class NetDevices
{
    /// <summary>Mac NetScanner.devices: online devices from the scan facts + remembered devices that are not there (offline rows).</summary>
    public static List<NetDevice> Build(ScanState s, DeviceStore store, Func<byte[], string?> vendorOf, DateTime now)
    {
        lock (s.Gate)
        {
            if (s.Info is not { } info) return [];
            var ips = new HashSet<uint>(s.Found.Keys);
            ips.UnionWith(s.Bonjour.Keys.Where(s.Targets.Contains));
            ips.UnionWith(s.Upnp.Keys.Where(s.Targets.Contains));
            if (s.IncludeMe && (ips.Count > 0 || !s.Scanning)) ips.Add(info.Ip);
            var list = ips.Select(ip => One(s, info, store, vendorOf, now, ip)).ToList();
            // Remembered devices that are not seen now (this network only, and only when the ARP table was really readable)
            if (!s.CustomRange && s.ArpDone && !s.Blocked)
            {
                var seen = list.Where(d => d.Mac != null).Select(d => d.MacText).ToHashSet();
                foreach (var (key, k) in store.Devices.OrderBy(x => x.Key, StringComparer.Ordinal))
                    if (!seen.Contains(key) && (k.Net == null || k.Net == info.NetworkKey)) list.Add(Offline(key, k, vendorOf));
            }
            return list;
        }
    }

    static NetDevice One(ScanState s, ScanInfo info, DeviceStore store, Func<byte[], string?> vendorOf, DateTime now, uint ip)
    {
        var d = new NetDevice { Id = NetAddress.IpString(ip), Ip = ip, IsMe = ip == info.Ip, IsRouter = ip == info.Router, Remote = !info.IsLocal(ip) };
        s.Found.TryGetValue(ip, out var f); s.Bonjour.TryGetValue(ip, out var bj); s.Upnp.TryGetValue(ip, out var up);
        d.Mac = d.IsMe ? info.Mac : f?.Mac;
        if (!d.IsMe && f != null) { d.Ms = f.Ms; d.Loss = f.Loss; d.ViaTcp = f.ViaTcp; }
        d.Services = bj?.Types.ToHashSet() ?? [];
        d.PrivateMac = d.Mac != null && NetAddress.IsPrivateMac(d.Mac);
        d.Vendor = d.PrivateMac ? "Private address" : (d.Mac != null ? vendorOf(d.Mac) : null) ?? Cut(up?.Manufacturer, 40) ?? "–";
        d.Model = NetNames.ModelText(bj?.Model, up);
        var known = d.Mac != null ? store.Get(NetAddress.MacString(d.Mac)) : null;
        d.Custom = known?.Custom;
        var cands = new List<(string, string?)> { ("Your name", known?.Custom), ("This PC", d.IsMe ? info.ComputerName : null), ("Bonjour", bj?.Name) };
        s.Names.TryGetValue(ip, out var nm);
        foreach (var src in new[] { "NetBIOS", "mDNS", "DNS" }) cands.Add((src, nm != null && nm.TryGetValue(src, out var n) ? n : null));
        cands.Add(("UPnP", up?.FriendlyName)); cands.Add(("Remembered", known?.Name));
        var seen = new HashSet<string>();
        foreach (var (src, name) in cands) if (name != null && seen.Add(name.ToLowerInvariant())) d.Names.Add((src, name));
        var text = string.Join(" ", d.Names.Select(x => x.Name).Concat(new[] { up?.ModelName, up?.DeviceType, up?.Manufacturer }.OfType<string>()));
        d.Kind = Devices.Guess(text + " " + (bj?.Model ?? ""), d.Vendor == "–" ? "" : d.Vendor, [], d.IsRouter, d.IsMe, d.PrivateMac, d.Services);
        d.Name = d.Names.Count > 0 ? d.Names[0].Name : Devices.FallbackName(d.Vendor == "–" ? null : d.Vendor, d.Kind, d.IsRouter, d.PrivateMac);
        if (s.Ports.TryGetValue(ip, out var p)) d.Ports = p;
        d.CheckingPorts = s.Checking.Contains(ip);
        d.FirstSeen = known?.First; d.LastSeen = known?.Last;
        d.IsNew = known != null && store.IsNew(known, now);
        return d;
    }

    static NetDevice Offline(string key, KnownDevice k, Func<byte[], string?> vendorOf)
    {
        var mac = NetAddress.ParseMac(key);
        var d = new NetDevice { Id = "off " + key, Ip = k.Ip is { } t ? NetAddress.ParseIp(t) ?? 0 : 0, Mac = mac, Online = false, Custom = k.Custom, FirstSeen = k.First, LastSeen = k.Last };
        d.PrivateMac = mac != null && NetAddress.IsPrivateMac(mac);
        d.Vendor = d.PrivateMac ? "Private address" : (mac != null ? vendorOf(mac) : null) ?? "–";
        if (k.Custom != null) d.Names.Add(("Your name", k.Custom));
        if (k.Name != null && !d.Names.Any(x => x.Name.Equals(k.Name, StringComparison.OrdinalIgnoreCase))) d.Names.Add(("Remembered", k.Name));
        d.Kind = Devices.Guess(string.Join(" ", d.Names.Select(x => x.Name)), d.Vendor == "–" ? "" : d.Vendor, [], false, false, d.PrivateMac);
        d.Name = d.Names.Count > 0 ? d.Names[0].Name : Devices.FallbackName(d.Vendor == "–" ? null : d.Vendor, d.Kind, false, d.PrivateMac);
        return d;
    }

    static string? Cut(string? s, int n) => s == null ? null : s.Length <= n ? s : s[..n];
}
