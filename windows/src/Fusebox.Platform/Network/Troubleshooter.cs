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
        const int n = 10;
        var dns = NetProbe.FreshDns(ct);
        var router = net.Gateway is { } g ? NetProbe.PingSeries(NetProbe.ToAddress(g), n, 200, ct) : Task.FromResult<PingStats>(null!);
        var inetT = NetProbe.PingSeries(Cloudflare, n, 200, ct);
        var https = CheckHttps(ct);
        var inet = await inetT;
        if (inet.Got == 0)   // ICMP blocked: TCP 443 instead (like the Mac)
        {
            var tcp = new List<double>();
            for (int i = 0; i < 5; i++) if (await NetProbe.TcpConnect(Cloudflare, 443, 1000, ct) is { } ms) tcp.Add(ms);
            if (tcp.Count > 0) inet = PingStats.From(tcp, 5);
        }
        var d = new DiagInput(true, NetAddress.IpString(net.Ip), net.Gateway != null, await router, inet, await dns, await https, net.Wired,
            wifi?.Rssi, null, wifi?.Band, wifi?.Channel);
        return (Diagnosis.Find(d), Diagnosis.Steps(d), d);
    }

    /// <summary>Windows' own connectivity-check page.</summary>
    static async Task<bool?> CheckHttps(CancellationToken ct)
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var r = await c.GetAsync("https://www.msftconnecttest.com/connecttest.txt", ct);
            return r.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}
