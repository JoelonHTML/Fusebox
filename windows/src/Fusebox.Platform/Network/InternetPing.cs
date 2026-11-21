using System.Net;
using System.Net.Sockets;
using Fusebox.Core.Network;

namespace Fusebox.Platform.Network;

/// <summary>Quality test and live ping of the internet panel. Pings go to the router and to <c>pingHost</c> ("" = no internet traffic at all).</summary>
public static class InternetPing
{
    public sealed record Quality(PingStats? Router, PingStats? Internet, bool ViaTcp, double? DnsMs, IPAddress? Target);

    /// <summary>The ping host as an IPv4 address: a literal or the first A record; null = off or not found.</summary>
    public static async Task<IPAddress?> ResolveHost(string host, CancellationToken ct)
    {
        if (host.Length == 0) return null;
        if (IPAddress.TryParse(host, out var ip)) return ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
        try { return (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>10 ICMP pings 0.2 s apart to router and host; with no ICMP answer from the host, 10 TCP connects to host:443. Plus the DNS lookup time of apple.com.</summary>
    public static async Task<Quality> QualityAsync(LocalNet net, string host, CancellationToken ct)
    {
        var target = await ResolveHost(host, ct);
        Task<double?> dns = Task.FromResult<double?>(null);
        if (host.Length > 0)
            dns = Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { await Dns.GetHostAddressesAsync("apple.com", AddressFamily.InterNetwork, ct); return (double?)sw.Elapsed.TotalMilliseconds; }
                catch (OperationCanceledException) { throw; }
                catch { return null; }
            }, ct);
        var routerT = net.Gateway is { } g ? NetProbe.PingSeries(NetProbe.ToAddress(g), 10, 200, ct) : Task.FromResult<PingStats>(null!);
        var inetT = target != null ? NetProbe.PingSeries(target, 10, 200, ct) : Task.FromResult<PingStats>(null!);
        var router = net.Gateway == null ? null : await routerT;
        var inet = target == null ? null : await inetT;
        bool tcpUsed = false;
        if (target != null && inet is { Got: 0 })
        {
            var tcp = new List<double>();
            for (int i = 0; i < 10; i++) if (await NetProbe.TcpConnect(target, 443, 1000, ct) is { } ms) tcp.Add(ms);
            if (tcp.Count > 0) { inet = PingStats.From(tcp, 10); tcpUsed = true; }
        }
        return new Quality(router, inet, tcpUsed, await dns, target);
    }

    /// <summary>One reply-time sample for the live graph (null = no answer). Pings are sent together.</summary>
    public static async Task<(double? Router, double? Internet)> Tick(LocalNet net, IPAddress? target, bool viaTcp, CancellationToken ct)
    {
        var r = net.Gateway is { } g ? NetProbe.PingOnce(NetProbe.ToAddress(g), 1000, ct) : Task.FromResult<double?>(null);
        var i = target == null ? Task.FromResult<double?>(null) : viaTcp ? NetProbe.TcpConnect(target, 443, 1000, ct) : NetProbe.PingOnce(target, 1000, ct);
        return (await r, await i);
    }
}
