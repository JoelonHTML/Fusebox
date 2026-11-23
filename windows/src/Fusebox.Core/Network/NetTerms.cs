using static Fusebox.Core.Strings;

namespace Fusebox.Core.Network;

/// <summary>Every value on the network cards has a term with a title and a plain explanation (hover on the value, and all of a card's terms under its ⓘ).
/// Mac NetTerm: same English texts, so the Mac translations apply; Windows-only wording is in Strings/network.*.strings. No noise/SNR on Windows.</summary>
public enum NetTerm
{
    Router, Internet, Live, Jitter, Lost, P95, DnsLookup,
    Type, Vpn, Metered, Ipv4, Ipv6, Gateway, DnsServers, Ipv6Internet, PublicIp, Profile, EncryptedDns,
    Signal, LinkSpeed, Channel, Standard, Cable,
    Download, Upload, Delay, BusyDelay, Stability, Kinds,
}

public static class NetTerms
{
    public static string Title(NetTerm t) => t switch
    {
        NetTerm.Router => L("Router"), NetTerm.Internet => "Internet", NetTerm.Live => L("Live graph"), NetTerm.Jitter => L("Jitter"),
        NetTerm.Lost => L("Lost"), NetTerm.P95 => "95%", NetTerm.DnsLookup => L("DNS lookup"), NetTerm.Type => L("Type"), NetTerm.Vpn => "VPN",
        NetTerm.Metered => L("Metered"), NetTerm.Ipv4 => "IPv4", NetTerm.Ipv6 => "IPv6", NetTerm.Gateway => L("Router address"), NetTerm.DnsServers => "DNS",
        NetTerm.Ipv6Internet => L("IPv6 internet"), NetTerm.PublicIp => L("Public IP"), NetTerm.Profile => L("Network"), NetTerm.EncryptedDns => L("Encrypted DNS"),
        NetTerm.Signal => L("Signal"), NetTerm.LinkSpeed => L("Link speed"), NetTerm.Channel => L("Channel"), NetTerm.Standard => L("Standard"), NetTerm.Cable => L("Cable"),
        NetTerm.Download => L("Download"), NetTerm.Upload => L("Upload"), NetTerm.Delay => L("Delay"), NetTerm.BusyDelay => L("Busy delay"),
        NetTerm.Stability => L("Stability"), NetTerm.Kinds => L("Quick and Full"),
        _ => "",
    };

    public static string Text(NetTerm t) => t switch
    {
        NetTerm.Router => L("How fast your router answers: this is your own Wi-Fi or cable. Below 10 ms is great; above 30 ms points to Wi-Fi interference or a busy router."),
        NetTerm.Internet => L("How fast a server on the internet answers, through your provider. Below 20 ms is very good and below 50 ms fine; above 100 ms you notice it in calls and games."),
        NetTerm.Live => L("Reply times of the internet (line) and your router (thin line) over the last two minutes. Spikes are moments of delay; a flat, low line is best."),
        NetTerm.Jitter => L("How much the delay changes from one message to the next. Low jitter means a steady line. Above about 30 ms, calls and video can stutter."),
        NetTerm.Lost => L("The share of messages that never came back. 0% is normal. Even 1 or 2% makes calls break up and pages load slowly."),
        NetTerm.P95 => L("95 out of 100 replies were at least this fast. It shows the slow moments that an average hides."),
        NetTerm.DnsLookup => L("How long it takes to look up a name like apple.com. Below 50 ms is good; above 150 ms websites are slow to start loading."),
        NetTerm.Type => L("How this PC is connected: Wi-Fi, a cable (Ethernet) or a VPN. A cable is usually faster and steadier than Wi-Fi."),
        NetTerm.Vpn => L("On means (some of) your traffic goes through an encrypted tunnel to a VPN server. That adds a little delay and can lower the speed."),
        NetTerm.Metered => L("Yes on mobile data, a phone hotspot or with a data limit set. Windows and apps then hold back big downloads and backups."),
        NetTerm.Ipv4 => L("This PC's address on your own network, handed out by the router. The internet doesn't see it."),
        NetTerm.Ipv6 => L("This PC's newer, longer address, if your network supports IPv6. \"none\" is common and not a problem."),
        NetTerm.Gateway => L("The address of your router, the box between your network and the internet. Open it in a browser to reach the router's settings."),
        NetTerm.DnsServers => L("The servers that turn names like apple.com into addresses. Usually your router or provider; you can change them in Settings › Network & internet."),
        NetTerm.Ipv6Internet => L("Whether you can reach the internet over IPv6. Nice to have; without it everything still works over IPv4."),
        NetTerm.PublicIp => L("The address the internet sees for your whole home or office, given by your provider. Fusebox only asks Cloudflare when you click."),
        NetTerm.Profile => L("How Windows treats this network. Private: other devices can find this PC. Public: it hides, which is safer in cafés and hotels."),
        NetTerm.EncryptedDns => L("Whether name lookups are sent encrypted, so others on the network can't see which sites you look up. Windows 11 can turn this on by itself."),
        NetTerm.Signal => L("Wi-Fi signal strength in dBm; closer to 0 is stronger. −50 is excellent, −60 good, below −70 weak."),
        NetTerm.LinkSpeed => L("The speed between this PC and the router (or switch), not your internet speed. It's usually higher than a speed test shows; on Wi-Fi it drops when the signal is weak."),
        NetTerm.Channel => L("The radio channel, band and width. 5 and 6 GHz are faster and less crowded; 2.4 GHz reaches further but is often busy. Wider channels are faster."),
        NetTerm.Standard => L("The Wi-Fi generation in use. Wi-Fi 6 and 7 are faster and cope better with many devices than Wi-Fi 4 or 5."),
        NetTerm.Cable => L("A cable has no signal or interference to worry about, so it's usually faster and steadier than Wi-Fi. The link speed shows what the cable and port can do."),
        NetTerm.Download => L("How much data per second comes in: streaming, websites, downloads. Compare it with your subscription; over Wi-Fi you often get less than over a cable."),
        NetTerm.Upload => L("How much data per second goes out: video calls, sending files, backups. Usually lower than download."),
        NetTerm.Delay => L("The delay to the test server while the line is quiet. Below 20 ms is very good, below 50 ms fine."),
        NetTerm.BusyDelay => L("The delay while the line is full with a download or upload. If it rises a lot compared with the normal delay, calls and games suffer whenever someone downloads. A router with smart queueing (SQM) fixes this."),
        NetTerm.Stability => L("How much the speed changed from second to second during the test. ±10% or less is steady; above ±25% the line is unsteady, often from Wi-Fi interference or a busy network."),
        NetTerm.Kinds => L("Quick takes about 10 seconds and gives a good estimate. Full takes about 30 seconds and also measures top speed, busy delay and stability."),
        _ => "",
    };

    // The ⓘ of each card lists these (Mac NetTerm.connection / wifi / wired / speed, plus the Windows extras)
    public static readonly NetTerm[] Connection = [NetTerm.Router, NetTerm.Internet, NetTerm.Live, NetTerm.Jitter, NetTerm.Lost, NetTerm.DnsLookup, NetTerm.Type, NetTerm.Vpn, NetTerm.Metered,
        NetTerm.Ipv4, NetTerm.Ipv6, NetTerm.Gateway, NetTerm.DnsServers, NetTerm.Ipv6Internet, NetTerm.Profile, NetTerm.EncryptedDns, NetTerm.PublicIp];
    public static readonly NetTerm[] Wifi = [NetTerm.Signal, NetTerm.LinkSpeed, NetTerm.Channel, NetTerm.Standard];
    public static readonly NetTerm[] Wired = [NetTerm.Cable, NetTerm.LinkSpeed];
    public static readonly NetTerm[] Speed = [NetTerm.Download, NetTerm.Upload, NetTerm.Delay, NetTerm.BusyDelay, NetTerm.Stability, NetTerm.Kinds];
}
