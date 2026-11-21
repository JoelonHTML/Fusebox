using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Fusebox.Core.Network;

namespace Fusebox.Platform.Network;

/// <summary>Our own connection: the adapter that carries the default route.</summary>
public sealed record LocalNet(string Name, string Description, bool IsWifi, uint Ip, int Prefix, uint? Gateway, string[] Dns, byte[]? Mac,
    int IfIndex = 0, string Id = "", string[]? Ipv6 = null, bool Ipv6Router = false)
{
    public bool Wired => !IsWifi;
    /// <summary>Global IPv6 addresses (2000::/3).</summary>
    public string[] Ipv6Global => Ipv6 ?? [];
}

/// <summary>Small network probes: adapter info, ping, ARP, TCP port probe, fresh DNS lookup. Everything is async or blocking-on-a-worker; nothing runs unless called.</summary>
public static class NetProbe
{
    public static uint ToUint(IPAddress a) { var b = a.GetAddressBytes(); return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3]; }
    public static IPAddress ToAddress(uint ip) => new([(byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip]);

    static readonly string[] Virtual = ["virtual", "vmware", "hyper-v", "vethernet", "vpn", "loopback", "bluetooth", "tap-", "tunnel", "wintun", "wireguard", "docker", "wsl"];

    /// <summary>All usable IPv4 connections, the one with a default gateway first. Virtual adapters (Hyper-V, VPN, VMware…) only when nothing else is there.</summary>
    public static List<LocalNet> Connections()
    {
        var list = new List<(LocalNet Net, bool Virtual)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var uni = props.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (uni == null) continue;
                var gw = props.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)).Select(g => (uint?)ToUint(g.Address)).FirstOrDefault();
                var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray();
                var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
                var v6 = props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && (u.Address.GetAddressBytes()[0] & 0xE0) == 0x20).Select(u => u.Address.ToString()).ToArray();
                var v6Router = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6);
                int ifIndex = 0;
                try { ifIndex = props.GetIPv4Properties()?.Index ?? 0; } catch { }
                var net = new LocalNet(nic.Name, nic.Description, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211, ToUint(uni.Address), uni.PrefixLength, gw, dns, nic.GetPhysicalAddress().GetAddressBytes(), ifIndex, nic.Id, v6, v6Router);
                list.Add((net, Virtual.Any(text.Contains)));
            }
        }
        catch { }
        return list.OrderBy(x => x.Virtual).ThenBy(x => x.Net.Gateway == null).Select(x => x.Net).ToList();
    }

    public static LocalNet? Current() => Connections().FirstOrDefault();

    /// <summary>MAC of an address on our own subnet via ARP (no admin needed); null = nobody answered.</summary>
    public static byte[]? Arp(uint ip) => ArpNative.Query(ip);

    /// <summary>One ICMP echo in ms (rounded by Windows to whole ms; "&lt;1" reads 0); null = no reply.</summary>
    public static async Task<double?> PingOnce(IPAddress ip, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            using var p = new Ping();
            var r = await p.SendPingAsync(ip, timeoutMs).WaitAsync(ct);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static async Task<PingStats> PingSeries(IPAddress ip, int count, int everyMs, CancellationToken ct = default)
    {
        var rtts = new List<double>();
        for (int i = 0; i < count; i++)
        {
            var t = PingOnce(ip, 1000, ct);
            if (await t is { } ms) rtts.Add(ms);
            if (i < count - 1) await Task.Delay(everyMs, ct);
        }
        return PingStats.From(rtts, count);
    }

    /// <summary>Connect time in ms if the TCP port accepts the connection (refused counts as "host is there" only for the caller to decide: null here).</summary>
    public static async Task<double?> TcpConnect(IPAddress ip, int port, int timeoutMs, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(ip, port, cts.Token);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch { ct.ThrowIfCancellationRequested(); return null; }
    }

    /// <summary>Open ports among <paramref name="ports"/> (all at once) with the fastest connect time.</summary>
    public static async Task<(ushort[] Open, double? Ms)> ProbePorts(uint ip, IEnumerable<ushort> ports, int timeoutMs = 300, CancellationToken ct = default)
    {
        var addr = ToAddress(ip);
        var all = ports.ToArray();
        var res = await Task.WhenAll(all.Select(p => TcpConnect(addr, p, timeoutMs, ct)));
        var open = all.Where((_, i) => res[i] != null).ToArray();
        return (open, res.Min());
    }

    static async Task<T?> WithTimeout<T>(Task<T> t, int ms, CancellationToken ct)
    {
        var done = await Task.WhenAny(t, Task.Delay(ms, ct));
        if (done != t) { _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted); return default; }
        return await t;
    }

    /// <summary>Reverse DNS name (often the DHCP name the router knows), null if none within the timeout.</summary>
    public static async Task<string?> ReverseName(uint ip, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            var e = await WithTimeout(Dns.GetHostEntryAsync(ToAddress(ip)), timeoutMs, ct);
            return Fusebox.Core.Network.Devices.CleanHostname(e?.HostName);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Median ms of 3 lookups of random sub-names (never cached, so the DNS server really has to answer). "Doesn't exist" counts as an answer.
    /// null = no answer.
    /// </summary>
    public static async Task<double?> FreshDns(CancellationToken ct = default)
    {
        var rnd = Random.Shared;
        var names = new[] { "apple.com", "cloudflare.com", "google.com" }.Select(d => $"fb-{rnd.NextInt64():x}.{d}").ToArray();
        var res = await Task.WhenAll(names.Select(async n =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var t = Dns.GetHostAddressesAsync(n, AddressFamily.InterNetwork);
                var done = await Task.WhenAny(t, Task.Delay(3000, ct));
                if (done != t) { _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted); return (double?)null; }
                await t;
                return sw.Elapsed.TotalMilliseconds;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.HostNotFound || e.SocketErrorCode == SocketError.NoData) { return sw.Elapsed.TotalMilliseconds; }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }));
        return SpeedMath.Median(res.Where(r => r != null).Select(r => r!.Value).ToList());
    }
}

internal static unsafe partial class ArpNative
{
    [LibraryImport("iphlpapi.dll")]
    private static partial uint SendARP(uint destIp, uint srcIp, byte* mac, ref uint macLen);

    public static byte[]? Query(uint ip)
    {
        var mac = new byte[8];
        uint len = 8;
        fixed (byte* p = mac)
        {
            if (SendARP(BinaryPrimitives.ReverseEndianness(ip), 0, p, ref len) != 0 || len != 6) return null;
        }
        var m = mac.Take(6).ToArray();
        return m.Any(b => b != 0) ? m : null;
    }
}
