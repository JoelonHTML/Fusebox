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
            n["gatewayMac"] = mac == null ? null : NetAddress.MacString(mac);
            n["gatewayVendor"] = mac == null ? null : Devices.Vendor(mac);
        }
        n["dnsMs"] = NetProbe.FreshDns().GetAwaiter().GetResult();
        n["ouiEntries"] = Devices.Table.Count;
        if (Devices.Table.Count < 30_000) failures.Add("network: oui table");
        var w = Wifi.Read();
        n["wifi"] = w == null ? null : new JsonObject
        {
            ["connected"] = w.Connected, ["signal"] = w.SignalPercent, ["rssi"] = w.Rssi,
            ["channel"] = w.Channel, ["band"] = w.Band, ["nameHidden"] = w.NameHidden,
        };
    }
}
