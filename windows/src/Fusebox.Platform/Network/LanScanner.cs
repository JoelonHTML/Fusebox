using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Fusebox.Core.Network;
using static Fusebox.Core.Strings;

namespace Fusebox.Platform.Network;

/// <summary>
/// LAN scan (Mac NetScanner, §6.10): ICMP sweep (fills the ARP table) → neighbour table → names (mDNS, NetBIOS, reverse DNS) and latency →
/// UPnP descriptions → mDNS services keep listening until 7.5 s. Everything lands in a <see cref="ScanState"/>; the page builds the rows from it.
/// Runs only while the page is visible or after a click; cancel through the token (what was found stays).
/// </summary>
public static class LanScanner
{
    /// <summary>Starts fresh: clears the facts, fills the targets. Returns null when there is no network.</summary>
    public static ScanInfo? Prepare(ScanState s, LocalNet? net, IReadOnlyList<uint>? customRange, out List<uint> hosts)
    {
        hosts = [];
        lock (s.Gate)
        {
            s.Found = []; s.Bonjour = []; s.Names = []; s.Upnp = []; s.Ports = []; s.Checking = [];
            s.ArpDone = s.Blocked = false;
            s.Info = net == null ? null : new ScanInfo(net.Ip, net.Prefix, net.Gateway, net.Mac, Environment.MachineName);
            if (s.Info is not { } info) { s.Targets = []; return null; }
            s.CustomRange = customRange != null;
            var all = customRange?.ToList() ?? NetAddress.Hosts(net!.Ip, net.Prefix);
            s.IncludeMe = customRange == null || all.Contains(info.Ip);
            hosts = all.Where(h => h != info.Ip).ToList();
            s.Targets = hosts.ToHashSet();
            s.Scanning = true;
            return info;
        }
    }

    public static async Task ScanAsync(ScanState s, LocalNet net, IReadOnlyList<uint> hosts, Action changed, Action<string> phase, CancellationToken ct)
    {
        var info = s.Info!;
        uint me = info.Ip;
        var local = hosts.Where(info.IsLocal).ToHashSet();
        var start = Stopwatch.StartNew();
        var oui = Task.Run(() => Devices.Table, ct);
        phase(L("Pinging…"));

        // 0. in parallel: mDNS service browsing (local only), SSDP, maker list
        var bonjour = local.Count == 0 ? Task.CompletedTask : Browse(s, net, local, start, changed, ct);
        var ssdp = local.Count == 0 ? Task.FromResult(new Dictionary<uint, List<Uri>>()) : Ssdp(me, ct);

        // 1. ping everyone (round 2 only for silent addresses); that also fills the ARP table
        var rtt = new Dictionary<uint, double>();
        bool blocked = await Sweep(hosts, rtt, ct);
        await oui;
        ct.ThrowIfCancellationRequested();
        var arp = await Task.Run(() => ArpTable.Read(net.IfIndex), ct);
        lock (s.Gate)
        {
            foreach (var e in arp.Where(e => local.Contains(e.Ip))) s.Found[e.Ip] = new FoundHost { Mac = e.Mac };
            foreach (var (ip, ms) in rtt) { if (!s.Found.TryGetValue(ip, out var f)) s.Found[ip] = f = new FoundHost(); f.Ms = ms; }
            foreach (var ip in s.Bonjour.Keys) if (hosts.Contains(ip) && !s.Found.ContainsKey(ip)) s.Found[ip] = new FoundHost();
            s.ArpDone = true;
            // local hosts answered but the ARP table is empty: a firewall or security suite is in the way
            s.Blocked = blocked || (arp.Count == 0 && rtt.Keys.Any(local.Contains));
        }
        changed();

        // 1b. outside our subnet there is no ARP: knock on 80 and 443 (an answer or a refusal both mean "someone is there")
        List<uint> quiet;
        lock (s.Gate) quiet = hosts.Where(h => !local.Contains(h) && !s.Found.ContainsKey(h)).ToList();
        if (quiet.Count > 0)
        {
            phase(L("Trying quiet addresses…"));
            await Parallel.ForEachAsync(quiet, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (ip, t) =>
            {
                foreach (ushort port in new ushort[] { 80, 443 })
                {
                    var (present, _, ms) = await NetSockets.TcpKnock(ip, port, 400, t);
                    if (!present) continue;
                    lock (s.Gate) s.Found[ip] = new FoundHost { Ms = ms, ViaTcp = true };
                    break;
                }
            });
            changed();
        }

        // 2. names (mDNS, NetBIOS, DNS) and latency (3 pings) at the same time
        phase(L("Looking up names…"));
        List<uint> alive;
        lock (s.Gate) alive = s.Found.Keys.Where(k => k != me).ToList();
        var near = alive.Where(local.Contains).ToList();
        var mdns = near.Count == 0 ? Task.CompletedTask : MdnsReverse(s, near, me, ct);
        var nb = NetBios(s, alive, ct);
        var dns = ReverseDns(s, alive, ct);
        var lat = await Latency(alive, ct);
        lock (s.Gate)
            foreach (var (ip, list) in lat)
                if (list.Count > 0 && s.Found.TryGetValue(ip, out var f)) { f.Ms = list.Average(); f.Loss = (3 - Math.Min(list.Count, 3)) / 3.0; }
        // no ping answer: the connect time to a TCP port stands in for ms
        var silent = alive.Where(ip => !lat.TryGetValue(ip, out var l) || l.Count == 0).Where(ip => { lock (s.Gate) return s.Found[ip].ViaTcp != true; }).ToList();
        await Parallel.ForEachAsync(silent, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = ct }, async (ip, t) =>
        {
            double best = double.PositiveInfinity;
            var r = await Task.WhenAll(Devices.LatencyPorts.Select(p => NetSockets.TcpKnock(ip, p, 500, t)));
            foreach (var x in r) if (x.Present && x.Ms < best) best = x.Ms;
            if (double.IsFinite(best)) lock (s.Gate) { var f = s.Found[ip]; f.Ms = best; f.ViaTcp = true; }
        });
        await Task.WhenAll(mdns, nb, dns);
        changed();

        // 3. UPnP descriptions of what answered SSDP
        phase(L("Asking devices…"));
        var urls = await ssdp;
        await Upnp(s, urls.Where(u => hosts.Contains(u.Key)).ToDictionary(u => u.Key, u => u.Value), ct);
        changed();

        // 4. let the mDNS services trickle in until 7.5 s, then done
        await bonjour;
    }

    /// <summary>Round 1 to all, round 2 only to the silent ones (1 s later). True = every send failed (nothing could leave the PC).</summary>
    static async Task<bool> Sweep(IReadOnlyList<uint> hosts, Dictionary<uint, double> rtt, CancellationToken ct)
    {
        int failed = 0, sent = 0;
        async Task Round(IEnumerable<uint> ips) =>
            await Parallel.ForEachAsync(ips, new ParallelOptions { MaxDegreeOfParallelism = 96, CancellationToken = ct }, async (ip, t) =>
            {
                try
                {
                    using var p = new Ping();
                    var r = await p.SendPingAsync(NetProbe.ToAddress(ip), 850).WaitAsync(t);
                    Interlocked.Increment(ref sent);
                    if (r.Status == IPStatus.Success) lock (rtt) { rtt[ip] = rtt.TryGetValue(ip, out var old) ? Math.Min(old, r.RoundtripTime) : r.RoundtripTime; }
                    else if (r.Status is IPStatus.Unknown or IPStatus.NoResources or IPStatus.HardwareError) Interlocked.Increment(ref failed);
                }
                catch (OperationCanceledException) { throw; }
                catch { Interlocked.Increment(ref sent); Interlocked.Increment(ref failed); }
            });
        await Round(hosts);
        await Task.Delay(300, ct);
        List<uint> silent;
        lock (rtt) silent = hosts.Where(h => !rtt.ContainsKey(h)).ToList();
        await Round(silent);
        return sent > 0 && failed == sent && rtt.Count == 0;
    }

    static async Task<Dictionary<uint, List<double>>> Latency(IReadOnlyList<uint> ips, CancellationToken ct)
    {
        var res = ips.ToDictionary(ip => ip, _ => new List<double>());
        for (int round = 0; round < 3; round++)
        {
            await Parallel.ForEachAsync(ips, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (ip, t) =>
            {
                if (await NetProbe.PingOnce(NetProbe.ToAddress(ip), 1000, t) is { } ms) lock (res[ip]) res[ip].Add(ms);
            });
            if (round < 2) await Task.Delay(300, ct);
        }
        return res;
    }

    // ---- name sources ----

    static void AddName(ScanState s, uint ip, string source, string name)
    {
        lock (s.Gate)
        {
            if (!s.Names.TryGetValue(ip, out var d)) s.Names[ip] = d = [];
            d[source] = NetNames.Cap(name);
        }
    }

    static async Task MdnsReverse(ScanState s, List<uint> ips, uint me, CancellationToken ct)
    {
        var packets = new List<(uint, ushort, byte[])>();
        for (int i = 0; i < ips.Count; i++)
        {
            var q = NetPackets.DnsQuery(NetPackets.ReverseName(ips[i]), 12, (ushort)(i + 1));
            packets.Add((NetPackets.MdnsGroup, 5353, q)); packets.Add((ips[i], 5353, q));
        }
        await NetSockets.Exchange(packets, 1500 + packets.Count * 5, 5, me, 255, false,
            r => { foreach (var (ip, n) in NetPackets.ReverseAnswers(r.Data)) AddName(s, ip, "mDNS", n); }, ct);
    }

    static async Task NetBios(ScanState s, List<uint> ips, CancellationToken ct)
    {
        var q = NetPackets.NetbiosQuery();
        await NetSockets.Exchange(ips.Select(ip => (ip, (ushort)137, q)).ToList(), 1500 + ips.Count * 3, 3, 0, 64, false,
            r => { if (NetPackets.NetbiosName(r.Data) is { } n) AddName(s, r.Ip, "NetBIOS", n); }, ct);
    }

    static Task ReverseDns(ScanState s, List<uint> ips, CancellationToken ct) =>
        Parallel.ForEachAsync(ips, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (ip, t) =>
        {
            if (await NetProbe.ReverseName(ip, 1500, t) is { } n) AddName(s, ip, "DNS", n);
        });

    /// <summary>Ask for the 22 service types by multicast (answers come straight back) until 7.5 s after the start.</summary>
    static async Task Browse(ScanState s, LocalNet net, HashSet<uint> local, Stopwatch start, Action changed, CancellationToken ct)
    {
        var packets = new List<(uint, ushort, byte[])>();
        for (int round = 0; round < 2; round++)
            foreach (var type in NetNames.ServiceTypes) packets.Add((NetPackets.MdnsGroup, 5353, NetPackets.DnsQuery(type + ".local", 12)));
        int resolved = 0;
        await NetSockets.Exchange(packets, Math.Max(500, 7500 - (int)start.ElapsedMilliseconds), 80, net.Ip, 255, false, r =>
        {
            if (!local.Contains(r.Ip) || resolved > 400) return;
            foreach (var (type, instance, txt) in NetPackets.MdnsServices(r.Data))
                lock (s.Gate)
                {
                    resolved++;
                    if (!s.Bonjour.TryGetValue(r.Ip, out var b)) s.Bonjour[r.Ip] = b = new BonjourHost();
                    NetNames.AddService(b, type, NetNames.FriendlyName(instance, type, txt), NetNames.TxtModel(txt));
                }
        }, ct);
        changed();
    }

    static async Task<Dictionary<uint, List<Uri>>> Ssdp(uint me, CancellationToken ct)
    {
        var res = new Dictionary<uint, List<Uri>>();
        await NetSockets.Exchange([(NetPackets.SsdpGroup, 1900, NetPackets.SsdpSearch), (NetPackets.SsdpGroup, 1900, NetPackets.SsdpSearch)], 3000, 300, me, 2, false, r =>
        {
            if (!NetPackets.SsdpHeaders(r.Data).TryGetValue("location", out var loc) || NetPackets.LanUrl(loc, r.Ip) is not { } u) return;
            lock (res)
            {
                if (!res.TryGetValue(r.Ip, out var l)) res[r.Ip] = l = [];
                if (l.Count < 3 && !l.Contains(u)) l.Add(u);
            }
        }, ct);
        return res;
    }

    static async Task Upnp(ScanState s, Dictionary<uint, List<Uri>> urls, CancellationToken ct)
    {
        await Parallel.ForEachAsync(urls.Take(64), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (kv, t) =>
        {
            foreach (var u in kv.Value)
                if (await NetSockets.HttpGet(u, t) is { } body && NetPackets.ParseUpnp(body) is { } info) { lock (s.Gate) s.Upnp[kv.Key] = info; break; }
        });
    }

    // ---- per device ----

    /// <summary>Try the 15 common ports of one device (0.3 s); when it ignored ping, the fastest answer becomes its ms (via TCP).</summary>
    public static async Task CheckPorts(ScanState s, uint ip, CancellationToken ct)
    {
        lock (s.Gate) { if (!s.Checking.Add(ip)) return; }
        try
        {
            var r = await Task.WhenAll(Devices.Ports.Select(p => NetSockets.TcpKnock(ip, p, 300, ct)));
            lock (s.Gate)
            {
                s.Ports[ip] = Devices.Ports.Where((_, i) => r[i].Open).ToArray();
                var best = r.Where(x => x.Present).Select(x => (double?)x.Ms).Min();
                if (s.Found.TryGetValue(ip, out var f) && f.Ms == null && best != null) { f.Ms = best; f.ViaTcp = true; }
            }
        }
        finally { lock (s.Gate) s.Checking.Remove(ip); }
    }
}
