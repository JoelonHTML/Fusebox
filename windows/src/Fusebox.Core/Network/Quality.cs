using static Fusebox.Core.Strings;

namespace Fusebox.Core.Network;

/// <summary>Statistics of a series of pings. jitter = mean difference between consecutive replies (as on the Mac).</summary>
public sealed record PingStats(int Sent, int Got, double? Avg, double? Min, double? Max, double? Jitter)
{
    public static PingStats From(IReadOnlyList<double> rtts, int sent) => new(
        sent, Math.Min(rtts.Count, sent),
        rtts.Count == 0 ? null : rtts.Average(), rtts.Count == 0 ? null : rtts.Min(), rtts.Count == 0 ? null : rtts.Max(),
        rtts.Count > 1 ? Enumerable.Range(1, rtts.Count - 1).Average(i => Math.Abs(rtts[i] - rtts[i - 1])) : null);

    /// <summary>0..1</summary>
    public double Loss => Sent > 0 ? (double)(Sent - Got) / Sent : 0;
}

public enum SpeedKind { Quick, Full }

/// <summary>Speed test math (same numbers as the Mac's SpeedMath/SpeedKind).</summary>
public static class SpeedMath
{
    public static int Streams(SpeedKind k) => k == SpeedKind.Quick ? 4 : 6;
    public static double Seconds(SpeedKind k) => k == SpeedKind.Quick ? 5 : 15;
    /// <summary>Ramp-up seconds that don't count.</summary>
    public static int Skip(SpeedKind k) => k == SpeedKind.Quick ? 2 : 3;
    public static long Cap(SpeedKind k) => k == SpeedKind.Quick ? 600_000_000 : 2_000_000_000;

    public static double Mbps(long bytes, double seconds) => seconds > 0 ? bytes * 8 / seconds / 1e6 : 0;

    /// <summary>Per-second samples without the ramp-up; always keeps at least one.</summary>
    public static double[] Trimmed(IReadOnlyList<double> s, int skip) => s.Skip(Math.Min(skip, Math.Max(s.Count - 1, 0))).ToArray();

    public static double? Median(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return null;
        var s = v.OrderBy(x => x).ToArray();
        int m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
    }

    public static double? Mean(IReadOnlyList<double> v) => v.Count == 0 ? null : v.Average();

    /// <summary>Standard deviation / mean.</summary>
    public static double? Variation(IReadOnlyList<double> v)
    {
        if (v.Count < 2 || Mean(v) is not { } m || m <= 0) return null;
        return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count) / m;
    }

    public static string StabilityText(double cv)
    {
        var pct = "±" + (int)Math.Round(cv * 100) + "%";
        return cv < 0.1 ? L("steady (%@)", pct) : cv < 0.25 ? L("varies a little (%@)", pct) : L("unsteady (%@)", pct);
    }

    /// <summary>Delay under load ("bufferbloat") in plain words. Ok: true good, false problem, null take care.</summary>
    public static (string Text, bool? Ok) LoadVerdict(double idle, double loaded)
    {
        double extra = Math.Max(0, loaded - idle);
        var ms = "+" + (int)Math.Round(extra) + " ms";
        if (extra < 30) return (L("Delay barely rises while the line is busy (%@) → fine for calls and games", ms), true);
        if (extra < 100) return (L("Delay rises while the line is busy (%@) → calls may stutter during big downloads", ms), null);
        return (L("Delay rises a lot while the line is busy (%@, \"bufferbloat\") → calls and games suffer during downloads; a router with smart queueing (SQM) helps", ms), false);
    }
}

/// <summary>Wi-Fi helpers: frequency → band/channel, PHY type names, signal words.</summary>
public static class WifiMath
{
    public static string? Band(uint khz) => khz switch { >= 2_400_000 and < 2_500_000 => "2.4 GHz", >= 4_900_000 and < 5_900_000 => "5 GHz", >= 5_925_000 and <= 7_125_000 => "6 GHz", _ => null };

    public static int? Channel(uint khz) => Band(khz) switch
    {
        "2.4 GHz" => khz == 2_484_000 ? 14 : (int)((khz - 2_407_000) / 5000),
        "5 GHz" => (int)((khz - 5_000_000) / 5000),
        "6 GHz" => (int)((khz - 5_950_000) / 5000),
        _ => null,
    };

    /// <summary>DOT11_PHY_TYPE → name.</summary>
    public static string? Phy(uint t) => t switch
    {
        4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "Wi-Fi 4 (802.11n)", 8 => "Wi-Fi 5 (802.11ac)", 10 => "Wi-Fi 6 (802.11ax)", 11 => "Wi-Fi 7 (802.11be)", _ => null,
    };

    public static string Word(int rssi) => rssi >= -60 ? L("good") : rssi >= -70 ? L("fair") : L("weak");

    /// <summary>Windows "signal quality" 0..100 → approximate dBm (the documented linear mapping: 0 = -100, 100 = -50).</summary>
    public static int QualityToRssi(int q) => -100 + Math.Clamp(q, 0, 100) / 2;
}

public enum FindCode { NotConnected, NoRouter, RouterDown, WeakSignal, CableUnstable, WifiUnstable, InternetDown, ProviderProblem, DnsFail, DnsSlow, WeakButWorks, AllGood }

/// <summary>One-sentence verdict (the Mac's "Find the problem") with the numbers underneath.</summary>
public sealed record Finding(FindCode Code, string Text, bool? Ok, string Numbers);

/// <summary>One line of the step-by-step list: Ok null = not tested because an earlier step failed.</summary>
public sealed record DiagStep(string Title, bool? Ok, string Detail, string? Advice);

public sealed record DiagInput(
    bool AdapterUp, string? Ipv4, bool HasGateway, PingStats? Router, PingStats Internet, double? DnsMs, bool? HttpsOk,
    bool Wired, int? Rssi = null, int? Snr = null, string? Band = null, int? Channel = null);

public static class Diagnosis
{
    static string Ms(double? v) => v is { } x ? (x < 1 ? "<1 ms" : (int)Math.Round(x) + " ms") : L("no reply");

    /// <summary>Port of NetScanner's find(): one verdict. Router null = no router known; Got 0 = no answer.</summary>
    public static Finding Find(DiagInput d)
    {
        string Lost(PingStats s) => s.Got > 0 && s.Loss > 0 ? L(" (%lld%% lost)", (int)Math.Round(s.Loss * 100)) : "";
        var nums = new List<string>
        {
            L("router %@", d.Router is { } r0 ? Ms(r0.Avg) + Lost(r0) : L("unknown")),
            L("internet %@", Ms(d.Internet.Avg) + Lost(d.Internet)),
            "DNS " + Ms(d.DnsMs),
        };
        if (d.Rssi is { } rs) nums.Add($"Wi-Fi {rs} dBm" + (d.Snr is { } sn ? $" · SNR {sn} dB" : "") + (d.Channel is { } ch ? $" · ch {ch}" : "") + (d.Band is { } b ? $" ({b})" : ""));
        Finding Say(FindCode c, string text, bool? ok) => new(c, text, ok, string.Join(", ", nums));

        var local = d.Wired ? L("network") : "Wi-Fi";
        bool weak = !d.Wired && (d.Rssi is < -72 || d.Snr is < 20);
        var weakText = L("Weak Wi-Fi signal%@: move closer to the router or use a cable", d.Rssi is { } w ? $" ({w} dBm)" : "");
        bool rDead = d.Router?.Got == 0, iDead = d.Internet.Got == 0;
        if (!d.AdapterUp || d.Ipv4 == null) return Say(FindCode.NotConnected, L("Not connected to a network: turn on Wi-Fi or plug in a cable"), false);
        if (d.Router == null && iDead) return Say(FindCode.NoRouter, L("No router found: reconnect to your network"), false);
        if (rDead && iDead)
            return weak ? Say(FindCode.WeakSignal, weakText, false)
                : Say(FindCode.RouterDown, d.Wired ? L("Router not responding: check the cable, then restart the router") : L("Router not responding: restart the router"), false);
        // Router ignores pings but the internet works: then the router measurement says nothing
        if (d.Router is { Got: > 0 } r && (r.Loss >= 0.1 || (r.Avg ?? 0) > 50 || (r.Jitter ?? 0) > 30))
        {
            if (weak) return Say(FindCode.WeakSignal, weakText, false);
            if (d.Wired) return Say(FindCode.CableUnstable, L("The cable link to your router is unstable: check the cable, then restart the router"), false);
            return Say(FindCode.WifiUnstable, L("Your Wi-Fi is unstable despite a good signal: interference or a busy router; restart the router") + (d.Band == "2.4 GHz" ? L(" or switch to 5 GHz") : ""), false);
        }
        if (iDead) return Say(FindCode.InternetDown, L("Your %@ is fine, but the internet doesn't answer: restart the modem, or your provider has an outage", local), false);
        var net = d.Internet;
        if (net.Loss >= 0.05 || (net.Avg ?? 0) > 100 || (net.Jitter ?? 0) > 30)
        {
            var what = net.Loss >= 0.05 ? L("loses data") : (net.Avg ?? 0) > 100 ? L("is slow") : L("is unsteady");
            return Say(FindCode.ProviderProblem, L("Your %@ is fine; your internet provider %@ (router %@, internet %@)", local, what, Ms(d.Router?.Avg), Ms(net.Avg)), false);
        }
        if (d.DnsMs is not { } dns) return Say(FindCode.DnsFail, L("Name lookups (DNS) fail: try 1.1.1.1 as DNS server in Network settings"), false);
        if (dns > 300) return Say(FindCode.DnsSlow, L("DNS is slow (%@): try 1.1.1.1 as DNS server in Network settings", Ms(dns)), false);
        if (weak) return Say(FindCode.WeakButWorks, L("Everything works, but the Wi-Fi signal is weak%@: moving closer to the router helps", d.Rssi is { } w2 ? $" ({w2} dBm)" : ""), null);
        return Say(FindCode.AllGood, L("No problem found: %@, router, DNS and internet are all fine", local), true);
    }

    /// <summary>The step list: adapter → IP → router → DNS → internet → HTTPS, each with advice when it fails.</summary>
    public static List<DiagStep> Steps(DiagInput d)
    {
        var steps = new List<DiagStep>();
        bool go = true;   // false once a step failed that makes later ones meaningless
        void Add(string title, bool? ok, string detail, string? advice) => steps.Add(new(title, ok, detail, ok == false ? advice : null));

        Add(L("Network adapter is connected"), d.AdapterUp, d.AdapterUp ? "" : L("No network connection"),
            d.Wired ? L("Check the cable and the router's lights, or turn on Wi-Fi.") : L("Turn on Wi-Fi or plug in a cable."));
        go = d.AdapterUp;
        bool hasIp = go && d.Ipv4 != null && !d.Ipv4.StartsWith("169.254.");
        Add(L("Has an IP address"), go ? hasIp : null, d.Ipv4 ?? "–",
            L("Your PC got no address from the router. Restart the router, then reconnect."));
        go = go && hasIp;

        bool? routerOk = !go ? null : d.Router == null ? false : d.Router.Got > 0;
        Add(L("Router answers"), routerOk, !go ? "" : d.Router == null ? L("No router found") : d.Router.Got > 0 ? Ms(d.Router.Avg) : L("no reply"),
            d.Router == null ? L("No router found: reconnect to your network")
                : d.Wired ? L("Check the cable, then restart the router.") : L("Move closer to the router or restart it. Some routers ignore pings; the internet check below tells more."));
        // a router that ignores pings isn't a dead end: carry on with the other tests
        bool dnsOk = go && d.DnsMs != null;
        Add(L("Name lookup (DNS) works"), go ? dnsOk : null, go ? Ms(d.DnsMs) + (d.DnsMs > 300 ? " · " + L("slow") : "") : "",
            L("Try 1.1.1.1 as DNS server in Network settings."));
        bool inetOk = go && d.Internet.Got > 0;
        Add(L("Internet answers (1.1.1.1)"), go ? inetOk : null, go ? Ms(d.Internet.Avg) : "",
            L("The internet doesn't answer: restart the modem, or your provider has an outage."));
        Add(L("Secure web pages open"), go ? d.HttpsOk : null, go ? (d.HttpsOk == true ? "HTTPS" : "") : "",
            L("Websites don't open although the internet answers: check a VPN, proxy or firewall."));
        return steps;
    }
}
