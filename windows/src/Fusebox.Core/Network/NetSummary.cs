using static Fusebox.Core.Strings;

namespace Fusebox.Core.Network;

/// <summary>Plain-language judgement of the internet panel (Mac InternetStats.verdict, §6.3.3) and the small texts around it.</summary>
public static class InternetVerdict
{
    static string Ms(double? v) => v is { } x ? (x < 1 ? "<1 ms" : (int)Math.Round(x, MidpointRounding.AwayFromZero) + " ms") : "–";
    static string Pct(double v) => (int)Math.Round(v * 100, MidpointRounding.AwayFromZero) + "%";

    /// <summary>ok: true good, false problem, null take care. Router/internet null = not measured; Got 0 = no answer.</summary>
    public static (string Text, bool? Ok) Verdict(bool wired, int? rssi, int? snr, PingStats? router, PingStats? internet, double? dnsMs, bool internetCheck)
    {
        bool weak = !wired && (rssi is < -70 || snr is < 20);
        bool rDead = router is { Got: 0 }, iDead = internetCheck && internet is { Got: 0 };
        if (rDead && iDead) return (L("No answer from the router or the internet → check the connection"), false);
        if (iDead) return (L("The router answers but the internet doesn't → problem at your provider or modem"), false);
        var r = rDead ? null : router; var i = internetCheck ? internet : null;
        double rJitter = wired ? 20 : 40;   // Wi-Fi jitters by itself (chip power saving): only above 40 ms say something, on a cable above 20
        bool rBad = r != null && (r.Loss >= 0.02 || (r.Avg ?? 0) > 30 || (r.Jitter ?? 0) > rJitter);
        bool iBad = i != null && (i.Loss >= 0.02 || (i.Avg ?? 0) > 100 || (i.Jitter ?? 0) > 30);
        var parts = new List<string>();
        if (r != null && rBad)
        {
            var what = r.Loss >= 0.02 ? L("%@ packet loss to the router", Pct(r.Loss)) : (r.Avg ?? 0) > 30 ? L("slow replies from the router (%@)", Ms(r.Avg)) : L("unsteady replies from the router (jitter %@)", Ms(r.Jitter));
            var cap = char.ToUpperInvariant(what[0]) + what[1..];
            parts.Add(weak ? L("Wi-Fi signal weak and %@ → move closer to the router or use a cable", what)
                : wired ? L("%@ on a cable → check the cable or restart the router", cap)
                : L("%@ → Wi-Fi interference or a busy router; restart it or use a cable", cap));
        }
        else if (i != null && iBad)
        {
            var what = i.Loss >= 0.02 ? L("%@ packet loss", Pct(i.Loss)) : (i.Jitter ?? 0) > 30 ? L("unsteady replies (jitter %@)", Ms(i.Jitter)) : L("slow replies (%@)", Ms(i.Avg));
            parts.Add(L("Your own network is fine, but the internet has %@ → most likely your provider", what));
        }
        else if (weak) parts.Add(L("Wi-Fi signal is weak (%@) but the connection works for now; moving closer helps", rssi is { } x ? $"{x} dBm" : L("low")));
        if (internetCheck && dnsMs is > 150) parts.Add(L("Name lookups are slow (%@) → try another DNS server", Ms(dnsMs)));
        if (parts.Count == 0)
        {
            var good = L("All good: ") + (r != null ? L("router %@", Ms(r.Avg)) : L("router doesn't answer pings"));
            if (i != null) good += L(", internet %@", Ms(i.Avg)); else if (!internetCheck) good += L(" (internet check is off in Settings)");
            return (good + L(", no packet loss"), true);
        }
        return (string.Join(". ", parts), rBad || iBad ? false : null);
    }

    /// <summary>"3 ms (2–5) · jitter 1 · 0% lost"; "no answer" with 0 replies; "…" while testing; "–" when never measured.</summary>
    public static string StatsText(PingStats? s, bool testing)
    {
        if (s == null) return testing ? "…" : "–";
        if (s.Got == 0 || s.Avg is not { } avg) return L("no answer");
        var t = Ms(avg);
        if (s.Min is { } lo && s.Max is { } hi) t += $" ({NetView.MsText(lo)}–{NetView.MsText(hi)})";
        if (s.Jitter is { } j) t += L(" · jitter %@", NetView.MsText(j));
        return t + L(" · %lld%% lost", (int)Math.Round(s.Loss * 100));
    }
}

/// <summary>Last N reply times for the live graph; a missed ping repeats the previous value (loss shows in the numbers), the very first miss = 0.</summary>
public sealed class RttRing(int capacity = 60)
{
    readonly List<double> v = [];
    public IReadOnlyList<double> Values => v;
    public void Push(double? ms)
    {
        v.Add(ms ?? (v.Count > 0 ? v[^1] : 0));
        if (v.Count > capacity) v.RemoveRange(0, v.Count - capacity);
    }
    public void Clear() => v.Clear();
    /// <summary>Y scale top: max of both series (at least 5 ms) × 1.2.</summary>
    public static double Top(IEnumerable<double> a, IEnumerable<double> b) => Math.Max(a.Concat(b).DefaultIfEmpty(0).Max(), 5) * 1.2;
}

/// <summary>Windows-only extras in the Connection section, in plain words.</summary>
public static class WinExtras
{
    /// <summary>"Public" / "Private" / "Domain"; null = unknown (registry Category 0/1/2).</summary>
    public static string? ProfileWord(int? category) => category switch
    {
        0 => L("Public (this PC hides from other devices)"), 1 => L("Private (a trusted network, this PC can be found)"), 2 => L("Domain (a work network)"), _ => null,
    };

    /// <summary>VPN adapters: PPP/tunnel interfaces or well-known VPN names. Virtual switches (Hyper-V, WSL, Docker) are not VPNs.</summary>
    public static bool IsVpn(string type, string description)
    {
        var d = description.ToLowerInvariant();
        if (d.Contains("hyper-v") || d.Contains("vethernet") || d.Contains("wsl") || d.Contains("docker") || d.Contains("virtualbox") || d.Contains("vmware")) return false;
        return type.Equals("Ppp", StringComparison.OrdinalIgnoreCase)
            || new[] { "vpn", "wireguard", "wintun", "tap-windows", "openvpn", "anyconnect", "nordlynx", "fortinet", "globalprotect", "pulse secure", "tailscale", "zerotier", "proton" }.Any(d.Contains);
    }

    /// <summary>Resolvers Windows 11 upgrades to encrypted DNS by itself (when "automatic" is on).</summary>
    public static readonly string[] AutoDohServers = ["1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112"];

    public static string OnOff(bool on) => on ? L("On") : L("Off");
    public static string YesNo(bool yes) => yes ? L("Yes") : L("No");
    public static string Metered(bool yes) => yes ? L("Yes: Windows and apps may hold back big downloads") : L("No");
}

/// <summary>Channel width from the Wi-Fi information elements of the connected access point (HT 61, VHT 192; 6 GHz / Wi-Fi 7 widths are not read).</summary>
public static class WifiWidth
{
    public static int? FromIes(ReadOnlySpan<byte> ies)
    {
        int? width = null;
        int i = 0;
        while (i + 2 <= ies.Length)
        {
            int id = ies[i], len = ies[i + 1];
            if (i + 2 + len > ies.Length) break;
            var body = ies.Slice(i + 2, len);
            if (id == 61 && len >= 2) width = (body[1] & 0x04) != 0 ? 40 : 20;                                   // HT operation: STA channel width bit
            else if (id == 192 && len >= 1 && width != null) width = body[0] switch { 1 => 80, 2 => 160, 3 => 160, _ => width };   // VHT operation
            i += 2 + len;
        }
        return width;
    }
}
