using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Fusebox.Core.Network;

namespace Fusebox.Platform.Network;

/// <summary>The Windows neighbour (ARP) table through GetIpNetTable2: MACs of everything that answered ARP lately; no admin rights needed.</summary>
public static unsafe partial class ArpTable
{
    [LibraryImport("iphlpapi.dll")] private static partial uint GetIpNetTable2(ushort family, out nint table);
    [LibraryImport("iphlpapi.dll")] private static partial void FreeMibTable(nint table);

    // MIB_IPNET_TABLE2: ULONG NumEntries (+4 padding), then 88-byte MIB_IPNET_ROW2:
    // SOCKADDR_INET Address @0 (family @0, IPv4 @4), InterfaceIndex @28, PhysicalAddress[32] @40, PhysicalAddressLength @72, State @80.
    const int Row = 88;

    public readonly record struct Entry(uint Ip, byte[] Mac, int IfIndex);

    /// <summary>IPv4 neighbours with a usable MAC (reachable, stale, delay, probe, permanent); no broadcast or multicast addresses. ifIndex 0 = all adapters.</summary>
    public static List<Entry> Read(int ifIndex = 0)
    {
        var res = new List<Entry>();
        nint t = 0;
        try
        {
            if (GetIpNetTable2(2, out t) != 0 || t == 0) return res;
            var p = (byte*)t;
            uint n = *(uint*)p;
            for (uint i = 0; i < Math.Min(n, 4096); i++)
            {
                var r = p + 8 + i * Row;
                if (*(ushort*)r != 2) continue;
                int state = *(int*)(r + 80), len = *(int*)(r + 72), idx = *(int*)(r + 28);
                if (state < 2 || len != 6 || (ifIndex != 0 && idx != ifIndex)) continue;   // 0 unreachable, 1 incomplete
                var mac = new ReadOnlySpan<byte>(r + 40, 6).ToArray();
                if ((mac[0] & 1) != 0 || mac.All(b => b == 0)) continue;                    // multicast / broadcast / empty
                uint ip = (uint)r[4] << 24 | (uint)r[5] << 16 | (uint)r[6] << 8 | r[7];
                res.Add(new(ip, mac, idx));
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (t != 0) FreeMibTable(t); }
        return res;
    }
}

/// <summary>UDP and TCP probes of the scan. Every reply is untrusted: ≤ 1024 replies, each ≤ 9000 bytes. Blocking work runs on worker threads, cancel through the token.</summary>
public static class NetSockets
{
    public sealed record Reply(uint Ip, byte[] Data);

    /// <summary>
    /// Sends the packets (<paramref name="gapMs"/> apart) and listens until <paramref name="waitMs"/> after the first one. Every reply goes to
    /// <paramref name="onReply"/> (any thread). <paramref name="iface"/> = our own address, so multicast leaves through the right adapter.
    /// Returns how many packets were sent (0 = blocked, e.g. by a firewall).
    /// </summary>
    public static Task<int> Exchange(IReadOnlyList<(uint Ip, ushort Port, byte[] Data)> packets, int waitMs, int gapMs, uint iface, int ttl, bool broadcast, Action<Reply>? onReply, CancellationToken ct) =>
        Task.Run(() =>
        {
            int sent = 0, got = 0;
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                try { s.IOControl(-1744830452, [0, 0, 0, 0], null); } catch { }   // SIO_UDP_CONNRESET: an ICMP "port unreachable" must not break receiving
                s.Bind(new IPEndPoint(IPAddress.Any, 0));
                if (broadcast) s.EnableBroadcast = true;
                if (iface != 0) try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, NetProbe.ToAddress(iface).GetAddressBytes()); } catch { }
                try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl); s.Ttl = (short)ttl; } catch { }
                s.ReceiveTimeout = 100;
            }
            catch { return 0; }
            var clock = Stopwatch.StartNew();
            var sender = Task.Run(() =>
            {
                foreach (var (ip, port, data) in packets)
                {
                    if (ct.IsCancellationRequested || clock.ElapsedMilliseconds > waitMs) break;
                    try { s.SendTo(data, new IPEndPoint(NetProbe.ToAddress(ip), port)); Interlocked.Increment(ref sent); } catch { }
                    if (gapMs > 0) Thread.Sleep(gapMs);
                }
            });
            var buf = new byte[9000];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (clock.ElapsedMilliseconds < waitMs && !ct.IsCancellationRequested && got < 1024)
            {
                try
                {
                    int n = s.ReceiveFrom(buf, ref any);
                    if (any is IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ep && onReply != null) { got++; onReply(new(NetProbe.ToUint(ep.Address), buf.AsSpan(0, n).ToArray())); }
                }
                catch (SocketException) { }   // timeout or connection reset: keep listening until the time is up
                catch (ObjectDisposedException) { break; }
            }
            try { sender.Wait(500); } catch { }
            return Volatile.Read(ref sent);
        }, ct);

    /// <summary>TCP connect: present = accepted or refused (a RST also means someone is there), open = accepted.</summary>
    public static async Task<(bool Present, bool Open, double Ms)> TcpKnock(uint ip, ushort port, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(NetProbe.ToAddress(ip), port, cts.Token);
            return (true, true, sw.Elapsed.TotalMilliseconds);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { return (true, false, sw.Elapsed.TotalMilliseconds); }
        catch { ct.ThrowIfCancellationRequested(); return (false, false, 0); }
    }

    /// <summary>Magic packet to the subnet broadcast and 255.255.255.255, UDP port 9. true = sent.</summary>
    public static async Task<bool> Wake(byte[] mac, uint broadcast, CancellationToken ct = default)
    {
        if (NetPackets.MagicPacket(mac) is not { } p) return false;
        return await Exchange([(broadcast, 9, p), (0xFFFFFFFF, 9, p)], 50, 0, 0, 1, true, null, ct) > 0;
    }

    /// <summary>First private-LAN page of a UPnP device (≤ 64 KB, no redirects, no proxy), null on any problem.</summary>
    public static async Task<byte[]?> HttpGet(Uri url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(2000);
        try
        {
            using var h = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
            using var req = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version10, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var r = await h.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!r.IsSuccessStatusCode) return null;
            await using var s = await r.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[65_537];
            int total = 0, n;
            while (total < buf.Length && (n = await s.ReadAsync(buf.AsMemory(total), cts.Token)) > 0) total += n;
            return total > 65_536 ? null : buf.AsSpan(0, total).ToArray();
        }
        catch { ct.ThrowIfCancellationRequested(); return null; }
    }
}
