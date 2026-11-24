using System.Text.Json.Nodes;
using Fusebox.Core.Network;
using Fusebox.Platform.Network;

/// <summary>Network readers on the real runner. Fails only when there is no IPv4 address at all (Wi-Fi/gateway/DNS may legitimately be missing on a VM).</summary>
public static class NetworkProbe
{
    public static void Run(JsonObject o, List<string> failures)
    {
        var conns = NetProbe.Connections();
        var net = conns.FirstOrDefault();
        var n = new JsonObject { ["connections"] = conns.Count };
        o["network"] = n;
        if (net == null) { failures.Add("network: no IPv4 address"); return; }
        n["name"] = net.Name; n["wifiAdapter"] = net.IsWifi;
        n["ip"] = NetAddress.IpString(net.Ip); n["prefix"] = net.Prefix;
        n["gateway"] = net.Gateway is { } g ? NetAddress.IpString(g) : null;
        n["dns"] = string.Join(",", net.Dns);
        if (net.Gateway is { } gw)
        {
            var ping = NetProbe.PingOnce(NetProbe.ToAddress(gw), 1000).GetAwaiter().GetResult();
            n["gatewayPingMs"] = ping;
            var mac = NetProbe.Arp(gw);
            n["gatewayMacFound"] = mac != null;   // never the address itself: probe output is a public CI log (PLAN §7.8)
            n["gatewayVendor"] = mac == null ? null : Devices.Vendor(mac);
        }
        n["dnsMs"] = NetProbe.FreshDns().GetAwaiter().GetResult();
        n["ouiEntries"] = Devices.Table.Count;
        if (Devices.Table.Count < 30_000) failures.Add("network: oui table");
        var w = Wifi.Read();
        n["wifi"] = w == null ? null : new JsonObject
        {
            ["connected"] = w.Connected, ["signal"] = w.SignalPercent, ["rssi"] = w.Rssi,
            ["channel"] = w.Channel, ["band"] = w.Band, ["widthMhz"] = w.WidthMhz, ["nameHidden"] = w.NameHidden,
        };
        // neighbour table (GetIpNetTable2): printed only, a VM may have nothing but its gateway
        var arp = ArpTable.Read(net.IfIndex);
        var best = NetProbe.BestInterface();
        n["bestIfIndex"] = best;   // GetBestInterface: the adapter Windows routes the internet through must come first
        if (best != 0 && conns.Any(c => c.IfIndex == best) && net.IfIndex != best) failures.Add("network: routed adapter not first");
        n["ifIndex"] = net.IfIndex; n["id"] = net.Id; n["ipv6"] = string.Join(",", net.Ipv6Global); n["ipv6Router"] = net.Ipv6Router;
        n["arpEntries"] = arp.Count;
        n["arpHasGateway"] = net.Gateway is { } g2 && arp.Any(e => e.Ip == g2);
        // Windows extras (profile, VPN, DoH, metered)
        var x = WinNet.Read(net);
        n["extras"] = new JsonObject { ["profileCategory"] = x.ProfileCategory, ["vpn"] = x.Vpn, ["encryptedDns"] = x.EncryptedDns, ["metered"] = x.Metered };
        // WinRT connection cost must not throw on any PC
        n["isMetered"] = WinNet.IsMetered();
        // raw sockets: a UDP packet to ourselves on a free port must at least leave (sent > 0)
        var sent = NetSockets.Exchange([(0x7F000001u, 9, new byte[] { 1 })], 100, 0, 0, 1, false, null, CancellationToken.None).GetAwaiter().GetResult();
        n["udpSent"] = sent;
        if (sent == 0) failures.Add("network: udp send");
    }
}
