using Fusebox.Core;
using Fusebox.Core.Network;
using static Fusebox.Core.Strings;

namespace Fusebox.Platform.Network;

public sealed class NetDevice
{
    public uint Ip;
    public byte[]? Mac;
    public string? Name;
    public string? Vendor;
    public bool PrivateMac, IsMe, IsRouter, ViaTcp;
    public double? Ms;
    public ushort[]? Ports;
    public DeviceKind Kind = DeviceKind.Unknown;

    public string IpText => NetAddress.IpString(Ip);
    public string MacText => Mac == null ? "–" : NetAddress.MacString(Mac);
    public string DisplayName => Name ?? Devices.FallbackName(Vendor, Kind, IsRouter, PrivateMac);
    public string VendorText => PrivateMac ? L("Private address") : Vendor ?? "–";

    public void Guess() => Kind = Devices.Guess(Name ?? "", Vendor ?? "", Ports ?? [], IsRouter, IsMe, PrivateMac);
}

/// <summary>
/// LAN scan: ARP per address (own subnet; gives the MAC without admin rights) or ping (other ranges), then names, makers and a quick port probe.
/// 64 dedicated worker threads (SendARP blocks ~1-3 s on an empty address). Cancel through the token.
/// </summary>
public static class LanScanner
{
    const int Workers = 64;

    public static async Task ScanAsync(LocalNet? net, IReadOnlyList<uint> targets, Action<NetDevice> changed, Action<string> phase, CancellationToken ct)
    {
        var found = new List<NetDevice>();
        var lockObj = new object();
        uint own = net?.Ip ?? 0;
        uint mask = net == null ? 0 : NetAddress.MaskFromPrefix(net.Prefix);
        var oui = await Task.Run(() => Devices.Table, ct);   // load the maker list off the UI thread

        void Found(NetDevice d)
        {
            lock (lockObj) found.Add(d);
            changed(d);
        }

        // 1. sweep
        phase(L("Pinging…"));
        int next = -1;
        var workers = Enumerable.Range(0, Math.Min(Workers, Math.Max(1, targets.Count))).Select(_ => Task.Factory.StartNew(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                int i = Interlocked.Increment(ref next);
                if (i >= targets.Count) return;
                uint ip = targets[i];
                bool local = net != null && (ip & mask) == (own & mask);
                var d = new NetDevice { Ip = ip, IsMe = ip == own, IsRouter = ip == net?.Gateway };
                if (d.IsMe) { d.Mac = net!.Mac; d.Name = Environment.MachineName; d.Ms = 0; }
                else if (local)
                {
                    d.Mac = NetProbe.Arp(ip);
                    if (d.Mac == null) continue;
                    d.Ms = NetProbe.PingOnce(NetProbe.ToAddress(ip), 400, ct).GetAwaiter().GetResult();
                }
                else
                {
                    d.Ms = NetProbe.PingOnce(NetProbe.ToAddress(ip), 400, ct).GetAwaiter().GetResult();
                    if (d.Ms == null) continue;
                }
                if (d.Mac != null)
                {
                    d.PrivateMac = NetAddress.IsPrivateMac(d.Mac);
                    d.Vendor = d.PrivateMac ? null : Devices.Vendor(d.Mac, oui);
                }
                d.Guess();
                Found(d);
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
        ct.ThrowIfCancellationRequested();

        // 2. names (reverse DNS), 16 at a time
        phase(L("Looking up names…"));
        var devices = found.ToArray();
        await Parallel.ForEachAsync(devices.Where(d => d.Name == null), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (d, t) =>
        {
            if (await NetProbe.ReverseName(d.Ip, 1500, t) is { } n) { d.Name = n; d.Guess(); changed(d); }
        });

        // 3. quick port probe (300 ms) for the type guess and "Open in browser"
        phase(L("Asking devices…"));
        await Parallel.ForEachAsync(devices.Where(d => !d.IsMe), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (d, t) => await CheckPorts(d, t, changed));
    }

    /// <summary>Probe the common ports of one device; when it ignored ping, the TCP connect time stands in for ms.</summary>
    public static async Task CheckPorts(NetDevice d, CancellationToken ct, Action<NetDevice> changed)
    {
        var (open, ms) = await NetProbe.ProbePorts(d.Ip, Devices.ProbePorts, 300, ct);
        d.Ports = open;
        if (d.Ms == null && ms != null) { d.Ms = ms; d.ViaTcp = true; }
        d.Guess();
        changed(d);
    }
}
