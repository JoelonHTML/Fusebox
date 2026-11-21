using Fusebox.Core.Network;

namespace Fusebox.Platform.Network;

/// <summary>"Find the problem": measures router, DNS, internet and HTTPS (~5 s, only after a click) and feeds the Core decision logic.</summary>
public static class Troubleshooter
{
    static readonly System.Net.IPAddress Cloudflare = System.Net.IPAddress.Parse("1.1.1.1");

    public static async Task<(Finding Finding, List<DiagStep> Steps, DiagInput Input)> RunAsync(CancellationToken ct)
    {
        var net = NetProbe.Current();
        if (net == null)
        {
            var none = new DiagInput(false, null, false, null, PingStats.From([], 1), null, null, false);
            return (Diagnosis.Find(none), Diagnosis.Steps(none), none);
        }
        var wifi = net.IsWifi ? await Task.Run(Wifi.Read, ct) : null;
        const int n = 20;   // 20 pings 0.25 s apart, as on the Mac
        var dns = NetProbe.FreshDns(ct);
        var router = net.Gateway is { } g ? NetProbe.PingSeries(NetProbe.ToAddress(g), n, 250, ct) : Task.FromResult<PingStats>(null!);
        var inetT = NetProbe.PingSeries(Cloudflare, n, 250, ct);
        var inet = await inetT;
        if (inet.Got == 0)   // ICMP blocked: TCP 443 instead (like the Mac)
        {
            var tcp = new List<double>();
            for (int i = 0; i < 5; i++) if (await NetProbe.TcpConnect(Cloudflare, 443, 1000, ct) is { } ms) tcp.Add(ms);
            if (tcp.Count > 0) inet = PingStats.From(tcp, 5);
        }
        var d = new DiagInput(true, NetAddress.IpString(net.Ip), net.Gateway != null, await router, inet, await dns, null, net.Wired,
            wifi?.Rssi, null, wifi?.Band, wifi?.Channel);
        return (Diagnosis.Find(d), Diagnosis.Steps(d), d);
    }
}
