using System.Text;
using Fusebox.Core.Network;

public class NetViewTests
{
    static NetDevice Dev(string name, string ip, bool online = true, double? ms = null, string vendor = "–", DeviceKind kind = DeviceKind.Unknown, byte[]? mac = null) =>
        new() { Id = ip + name, Name = name, Ip = NetAddress.ParseIp(ip)!.Value, Online = online, Ms = ms, Vendor = vendor, Kind = kind, Mac = mac };

    [Fact]
    public void WidthBandsAndColumns()
    {
        Assert.Equal(NetBand.Narrow, NetView.Band(469));
        Assert.Equal(NetBand.Vendor, NetView.Band(470));
        Assert.Equal(NetBand.Vendor, NetView.Band(599));
        Assert.Equal(NetBand.Kind, NetView.Band(600));
        Assert.Equal(NetBand.Kind, NetView.Band(759));
        Assert.Equal(NetBand.Mac, NetView.Band(760));
        // MAC visible by default at >= 760, hideable; Vendor/Type too; Name, IP and ms never
        Assert.True(NetView.Shows(NetBand.Mac, "", NetCol.Mac));
        Assert.False(NetView.Shows(NetBand.Kind, "", NetCol.Mac));
        Assert.False(NetView.Shows(NetBand.Mac, "mac", NetCol.Mac));
        Assert.False(NetView.Shows(NetBand.Mac, "vendor,kind", NetCol.Vendor));
        Assert.False(NetView.Shows(NetBand.Mac, "vendor,kind", NetCol.Kind));
        Assert.True(NetView.Shows(NetBand.Mac, "vendor,kind", NetCol.Mac));
        Assert.False(NetView.Shows(NetBand.Narrow, "", NetCol.Vendor));
        Assert.True(NetView.Shows(NetBand.Narrow, "mac,vendor,kind", NetCol.Ip));
        Assert.True(NetView.Shows(NetBand.Narrow, "mac,vendor,kind", NetCol.Ms));
        Assert.Equal(new[] { 104.0, 124, 118, 104, 42 }, new[] { NetCol.Ip, NetCol.Mac, NetCol.Vendor, NetCol.Kind, NetCol.Ms }.Select(NetView.Width));
    }

    [Fact]
    public void SortsOnlineFirstThenByColumn()
    {
        var list = new[] { Dev("tv10", "192.168.1.9", ms: 5), Dev("off", "192.168.1.1", online: false), Dev("tv2", "192.168.1.20", ms: 1), Dev("Alpha", "192.168.1.100") };
        Assert.Equal(new[] { "tv10", "tv2", "Alpha", "off" }, NetView.Sort(list, NetCol.Ip, true).Select(d => d.Name));
        Assert.Equal(new[] { "Alpha", "tv2", "tv10", "off" }, NetView.Sort(list, NetCol.Name, true).Select(d => d.Name));     // natural: tv2 before tv10
        Assert.Equal(new[] { "tv2", "tv10", "Alpha", "off" }, NetView.Sort(list, NetCol.Ms, true).Select(d => d.Name));      // unknown ms = infinity
        Assert.Equal(new[] { "Alpha", "tv2", "tv10", "off" }, NetView.Sort(list, NetCol.Ip, false).Select(d => d.Name));     // offline stays last when descending
        var m = new[] { Dev("a", "10.0.0.1", mac: [2, 0, 0, 0, 0, 1]), Dev("b", "10.0.0.2"), Dev("c", "10.0.0.3", mac: [1, 0, 0, 0, 0, 1]) };
        Assert.Equal(new[] { "c", "a", "b" }, NetView.Sort(m, NetCol.Mac, true).Select(d => d.Name));                          // unknown MAC last
    }

    [Fact]
    public void FilterCountsAndTexts()
    {
        var a = Dev("a", "10.0.0.1"); var o = Dev("o", "10.0.0.2", online: false); var n = Dev("n", "10.0.0.3"); n.IsNew = true;
        Assert.True(NetView.Matches(NetFilter.All, o) && NetView.Matches(NetFilter.Online, a) && !NetView.Matches(NetFilter.Online, o));
        Assert.True(NetView.Matches(NetFilter.New, n) && !NetView.Matches(NetFilter.New, a) && NetView.Matches(NetFilter.Offline, o));
        Assert.Equal("2 online · 1 new · 1 offline", NetView.CountText([a, o, n]));
        Assert.Equal("1 online", NetView.CountText([a]));
        Assert.Equal("–", NetView.MsText(null)); Assert.Equal("<1", NetView.MsText(0.4)); Assert.Equal("18", NetView.MsText(17.6));
        Assert.Equal("1 min ago", NetView.AgoText(TimeSpan.FromSeconds(20)));
        Assert.Equal("5 min ago", NetView.AgoText(TimeSpan.FromMinutes(5)));
        Assert.Equal("3 h ago", NetView.AgoText(TimeSpan.FromHours(3.5)));
        Assert.Equal("2 d ago", NetView.AgoText(TimeSpan.FromDays(2)));
    }

    [Fact]
    public void EmptyStatesInPriorityOrder()
    {
        string? E(bool net = true, bool auto = true, string s = "", bool scanning = false, NetFilter f = NetFilter.All, string? range = null) => NetView.EmptyText(net, auto, s, scanning, f, range);
        Assert.Equal("Not connected to a network", E(net: false));
        Assert.Equal("Press Scan to look for devices", E(net: false, auto: false));
        Assert.Equal("Not connected to a network", E(net: false, s: "x", scanning: true));      // network beats everything
        Assert.Equal("Nothing matches “tv”", E(s: "tv", scanning: true, f: NetFilter.New));      // search beats scanning
        Assert.Equal("Looking for devices…", E(scanning: true, f: NetFilter.New));
        Assert.Equal("No new devices", E(f: NetFilter.New, range: "10.0.0.0/24"));
        Assert.Equal("Nothing answered in 10.0.0.0/24", E(range: "10.0.0.0/24"));
        Assert.Equal("No devices found", E());
    }

    [Fact]
    public void SearchMatchesMacOnlyWhenTheColumnIsNotHidden()
    {
        var d = Dev("Living room", "192.168.1.42", vendor: "Sonos", mac: NetAddress.ParseMac("B8:27:EB:01:02:03")); d.Model = "One SL";
        d.Names.Add(("mDNS", "wohnzimmer"));
        foreach (var q in new[] { "", "living", "WOHNZ", "168.1.42", "sonos", "one sl" }) Assert.True(d.Matches(q, false), q);
        Assert.True(d.Matches("b8:27", true));
        Assert.False(d.Matches("b8:27", false));
        Assert.False(d.Matches("nope", true));
    }

    [Fact]
    public void RangePopoverTexts()
    {
        var info = new ScanInfo(NetAddress.ParseIp("192.168.1.23")!.Value, 24, null, null, "PC");
        Assert.Contains("Up to 4096 addresses", NetView.RangeHelp("", info));
        Assert.StartsWith("Not a range", NetView.RangeHelp("abc", info));
        Assert.StartsWith("The first address is higher", NetView.RangeHelp("192.168.1.80-10", info));
        Assert.StartsWith("That's 65534 addresses", NetView.RangeHelp("10.0.0.0/16", info));
        Assert.Equal("254 addresses", NetView.RangeHelp("192.168.1.0/24", info));
        Assert.Equal("1 address", NetView.RangeHelp("192.168.1.9", info));
        Assert.Contains("Outside your own network", NetView.RangeHelp("10.0.0.5-9", info));
        Assert.Equal(new[] { "c", "a", "b" }, NetView.PushRecent(["a", "b", "c"], "c"));
        Assert.Equal(5, NetView.PushRecent(["1", "2", "3", "4", "5"], "6").Count);
        Assert.Equal("6", NetView.PushRecent(["1", "2", "3", "4", "5"], "6")[0]);
        Assert.Equal(8, NetView.ExpectedSeconds(false, 5000, 5000));
        Assert.Equal(8, NetView.ExpectedSeconds(true, 100, 0));
        Assert.Equal(4 + 4096 * 0.003 + 4096 / 32.0 * 0.4, NetView.ExpectedSeconds(true, 4096, 4096), 6);
    }

    [Fact]
    public void LatencyTexts()
    {
        var d = Dev("x", "10.0.0.1");
        Assert.StartsWith("Doesn't answer", NetView.LatencyText(d));
        d.Ms = 18;
        Assert.Equal("Answers in 18 ms", NetView.LatencyText(d));
        d.Loss = 0; Assert.Equal("Answers in 18 ms · no packet loss (3 pings)", NetView.LatencyText(d));
        d.Loss = 1 / 3.0; Assert.Equal("Answers in 18 ms · 1 of 3 pings lost", NetView.LatencyText(d));
        d.ViaTcp = true; Assert.Equal("Answers in 18 ms (via a TCP connection; it ignores pings)", NetView.LatencyText(d));
    }
}

public class InternetVerdictTests
{
    static PingStats P(double ms, int got = 10, int sent = 10, double jitter = 0)
    {
        var r = Enumerable.Range(0, got).Select(i => ms + (i % 2 == 0 ? 0 : jitter)).ToList();
        return PingStats.From(r, sent);
    }

    [Fact]
    public void QuickVerdictRules()
    {
        (string Text, bool? Ok) V(bool wired = false, int? rssi = -55, PingStats? router = null, PingStats? inet = null, double? dns = 20, bool check = true) =>
            InternetVerdict.Verdict(wired, rssi, null, router ?? P(3), inet ?? P(15), dns, check);
        Assert.Equal("No answer from the router or the internet → check the connection", V(router: P(0, 0), inet: P(0, 0)).Text);
        Assert.Equal("The router answers but the internet doesn't → problem at your provider or modem", V(inet: P(0, 0)).Text);
        var bad = V(router: P(3, 8));   // 20 % loss
        Assert.False(bad.Ok);
        Assert.StartsWith("20% packet loss to the router → Wi-Fi interference", bad.Text);
        Assert.StartsWith("20% packet loss to the router on a cable", V(wired: true, router: P(3, 8)).Text);
        Assert.Equal("Wi-Fi signal weak and 20% packet loss to the router → move closer to the router or use a cable", V(rssi: -75, router: P(3, 8)).Text);
        Assert.StartsWith("Slow replies from the router (45 ms)", V(router: P(45)).Text);
        Assert.StartsWith("Your own network is fine, but the internet has 20% packet loss → most likely your provider", V(inet: P(15, 8)).Text);
        Assert.StartsWith("Your own network is fine, but the internet has slow replies (150 ms)", V(inet: P(150)).Text);
        var weak = V(rssi: -76);
        Assert.Null(weak.Ok); Assert.StartsWith("Wi-Fi signal is weak (-76 dBm) but the connection works", weak.Text);
        var dns = V(dns: 200);
        Assert.Null(dns.Ok); Assert.Equal("Name lookups are slow (200 ms) → try another DNS server", dns.Text);
        Assert.Equal("All good: router 3 ms, internet 15 ms, no packet loss", V().Text);
        Assert.True(V().Ok);
        Assert.Equal("All good: router 3 ms (internet check is off in Settings), no packet loss", V(check: false).Text);
        Assert.Equal("All good: router doesn't answer pings, internet 15 ms, no packet loss", V(router: P(0, 0)).Text);
        // Wi-Fi jitter below 40 ms is normal, on a cable above 20 it is not
        Assert.True(V(router: P(3, 10, 10, 30)).Ok == true);
        Assert.False(V(wired: true, router: P(3, 10, 10, 30)).Ok);
    }

    [Fact]
    public void StatsAndSummaryLines()
    {
        Assert.Equal("…", InternetVerdict.StatsText(null, true));
        Assert.Equal("–", InternetVerdict.StatsText(null, false));
        Assert.Equal("no answer", InternetVerdict.StatsText(P(0, 0), false));
        Assert.Equal("3 ms (3–3) · jitter <1 · 0% lost", InternetVerdict.StatsText(P(3), false));
        Assert.Equal("This PC 192.168.1.23 on Wi-Fi · signal good (-54 dBm)", InternetVerdict.ConnectionLine("192.168.1.23", "Wi-Fi", -54));
        Assert.Equal("This PC – on Ethernet", InternetVerdict.ConnectionLine(null, "Ethernet", null));
        Assert.Contains("fair (-65 dBm)", InternetVerdict.ConnectionLine("1.2.3.4", "Wi-Fi", -65));
    }

    [Fact]
    public void LiveRingRepeatsMissedPings()
    {
        var r = new RttRing(3);
        r.Push(null); r.Push(5); r.Push(null); r.Push(7); r.Push(8);
        Assert.Equal(new[] { 5.0, 5, 7 }.Concat([8]).Skip(1), r.Values);   // 60-point cap, here 3
        Assert.Equal(3, r.Values.Count);
        var first = new RttRing(); first.Push(null);
        Assert.Equal(new[] { 0.0 }, first.Values);
        Assert.Equal(6.0, RttRing.Top([1, 2], []), 6);                     // at least 5 ms, × 1.2
        Assert.Equal(60.0, RttRing.Top([10, 50], [20]), 6);
    }

    [Fact]
    public void WindowsExtrasInPlainWords()
    {
        Assert.StartsWith("Public", WinExtras.ProfileWord(0)); Assert.StartsWith("Private", WinExtras.ProfileWord(1)); Assert.StartsWith("Domain", WinExtras.ProfileWord(2));
        Assert.Null(WinExtras.ProfileWord(null));
        Assert.True(WinExtras.IsVpn("Ppp", "WAN Miniport (PPPOE)"));
        Assert.True(WinExtras.IsVpn("Unknown", "WireGuard Tunnel"));
        Assert.True(WinExtras.IsVpn("Ethernet", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter"));
        Assert.False(WinExtras.IsVpn("Ethernet", "Hyper-V Virtual Ethernet Adapter"));
        Assert.False(WinExtras.IsVpn("Wireless80211", "Intel(R) Wi-Fi 6 AX201 160MHz"));
        Assert.Equal("On", WinExtras.OnOff(true)); Assert.Equal("Off", WinExtras.OnOff(false));
        Assert.Equal("No", WinExtras.Metered(false)); Assert.StartsWith("Yes", WinExtras.Metered(true));
    }

    [Fact]
    public void WifiWidthFromInformationElements()
    {
        byte[] ht40 = [0, 3, 65, 66, 67, 61, 22, 36, 0x05, .. new byte[20]];      // SSID "ABC", then HT operation with the 40 MHz bit
        Assert.Equal(40, WifiWidth.FromIes(ht40));
        byte[] ht20 = [61, 22, 36, 0x00, .. new byte[20]];
        Assert.Equal(20, WifiWidth.FromIes(ht20));
        byte[] vht80 = [61, 22, 36, 0x05, .. new byte[20], 192, 5, 1, 42, 0, 0, 0];
        Assert.Equal(80, WifiWidth.FromIes(vht80));
        byte[] vht160 = [61, 22, 36, 0x05, .. new byte[20], 192, 5, 2, 50, 0, 0, 0];
        Assert.Equal(160, WifiWidth.FromIes(vht160));
        Assert.Null(WifiWidth.FromIes([0, 3, 65, 66, 67]));
        Assert.Null(WifiWidth.FromIes([61, 40, 1]));                               // truncated element: ignored
    }
}

public class SpeedHistoryTests
{
    [Fact]
    public void RoundTripLegacyAndLimit()
    {
        var a = new SpeedEntry(new DateTime(2026, 11, 21, 14, 3, 0, DateTimeKind.Utc), 412.4, 38, 11, SpeedKind.Full, 480, 50, 38, 0.04);
        var json = SpeedHistory.ToJson([a]);
        var back = SpeedHistory.Parse(json);
        Assert.Single(back);
        Assert.Equal(a, back[0]);
        var legacy = SpeedHistory.Parse("""[{"date":"2026-01-02T03:04:05Z","down":90.5,"up":20,"ping":11}]""");
        Assert.Equal(90.5, legacy[0].Down); Assert.Null(legacy[0].Kind); Assert.Null(legacy[0].DownPeak);
        Assert.Empty(SpeedHistory.Parse("not json")); Assert.Empty(SpeedHistory.Parse("{}")); Assert.Empty(SpeedHistory.Parse(""));
        Assert.Single(SpeedHistory.Parse("""[{"date":"x","down":1,"up":1},{"date":"2026-01-02T03:04:05Z","down":1,"up":1},{"nodate":1}]"""));
        var list = new List<SpeedEntry>();
        for (int i = 0; i < 15; i++) list = SpeedHistory.Push(list, a with { Down = i });
        Assert.Equal(10, list.Count); Assert.Equal(14, list[0].Down);   // newest first, max 10
    }

    [Fact]
    public void UnitTextEstimateAndBars()
    {
        Assert.Equal("412 Mbit/s", SpeedHistory.MbitText(412.4, false));
        Assert.Equal("51.5 MB/s", SpeedHistory.MbitText(412, true));
        Assert.Equal("1 Mbit/s", SpeedHistory.MbitText(0.6, false));
        Assert.Equal("at most 1.2 GB", SpeedHistory.Usage(SpeedKind.Quick, null));
        Assert.Equal("at most 4.0 GB", SpeedHistory.Usage(SpeedKind.Full, null));
        var last = new SpeedEntry(DateTime.UtcNow, 100, 20, 10);
        Assert.Equal(75_000_000L + 15_000_000, SpeedHistory.Estimate(SpeedKind.Quick, last));     // 100 Mbit/s × 1.2 × 5 s = 75 MB, up 15 MB
        Assert.Equal("about 90.0 MB", SpeedHistory.Usage(SpeedKind.Quick, last));
        Assert.Equal(1_200_000_000L, SpeedHistory.Estimate(SpeedKind.Quick, new SpeedEntry(DateTime.UtcNow, 10_000, 10_000, null)));   // the cap per direction
        Assert.Equal("Quick test", SpeedHistory.KindName(SpeedKind.Quick)); Assert.Equal("single connection", SpeedHistory.KindName(null));
        var bars = SpeedHistory.BarHeights([new(DateTime.UtcNow, 200, 1, null), new(DateTime.UtcNow, 100, 1, null), new(DateTime.UtcNow, 1, 1, null)]);
        Assert.Equal(new[] { 3.0, 12, 24 }, bars);                                                // oldest first, min 3, 24 × down / max
    }
}

public class DeviceStoreTests
{
    static readonly DateTime T0 = new(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);
    static NetDevice Online(string mac, string name = "Phone", bool me = false)
    {
        var d = new NetDevice { Mac = NetAddress.ParseMac(mac), Ip = NetAddress.ParseIp("192.168.1.40")!.Value, IsMe = me };
        d.Names.Add(("mDNS", name));
        return d;
    }

    [Fact]
    public void RemembersFirstAndLastAndNewRule()
    {
        var s = new DeviceStore(null, T0);
        var fresh = s.Remember([Online("AA:BB:CC:DD:EE:01"), Online("AA:BB:CC:DD:EE:02", me: true)], "192.168.1.0/24 via 192.168.1.1", T0.AddSeconds(30));
        Assert.Single(fresh);                                                      // not this PC
        Assert.Equal(2, s.Devices.Count);
        var k = s.Get("AA:BB:CC:DD:EE:01")!;
        Assert.Equal("Phone", k.Name); Assert.Equal("192.168.1.40", k.Ip); Assert.Equal("192.168.1.0/24 via 192.168.1.1", k.Net);
        Assert.False(s.IsNew(k, T0.AddSeconds(30)));                               // first scan: within 60 s of creation, nothing is new
        s.Remember([Online("AA:BB:CC:DD:EE:03")], "n", T0.AddMinutes(5));
        var k3 = s.Get("AA:BB:CC:DD:EE:03")!;
        Assert.True(s.IsNew(k3, T0.AddMinutes(5)));
        Assert.False(s.IsNew(k3, T0.AddDays(2)));                                  // only "today"
        // seen again: last moves, first stays
        s.Remember([Online("AA:BB:CC:DD:EE:03", "Tablet")], "n", T0.AddMinutes(9));
        Assert.Equal(T0.AddMinutes(5), s.Get("AA:BB:CC:DD:EE:03")!.First); Assert.Equal(T0.AddMinutes(9), s.Get("AA:BB:CC:DD:EE:03")!.Last);
        Assert.Empty(s.Remember([Online("AA:BB:CC:DD:EE:03")], "n", T0.AddMinutes(10)));
        // names from you / the old store / this PC are never stored as the found name
        var d = Online("AA:BB:CC:DD:EE:04"); d.Names.Clear(); d.Names.Add(("Your name", "Mine")); d.Names.Add(("Remembered", "Old"));
        s.Remember([d], "n", T0.AddMinutes(11));
        Assert.Null(s.Get("AA:BB:CC:DD:EE:04")!.Name);
    }

    [Fact]
    public void RenameForgetAndJsonFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "fusebox-nd-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new DeviceStore(path, T0);
            s.Remember([Online("AA:BB:CC:DD:EE:01"), Online("AA:BB:CC:DD:EE:02")], "net", T0.AddMinutes(2));
            s.Rename("AA:BB:CC:DD:EE:01", "  " + new string('x', 80) + "  ", T0.AddMinutes(3));
            Assert.Equal(64, s.Get("AA:BB:CC:DD:EE:01")!.Custom!.Length);
            s.Rename("AA:BB:CC:DD:EE:01", "  ", T0.AddMinutes(3));
            Assert.Null(s.Get("AA:BB:CC:DD:EE:01")!.Custom);
            s.Rename("AA:BB:CC:DD:EE:01", "Kitchen", T0.AddMinutes(3));
            s.Forget("AA:BB:CC:DD:EE:02");
            var loaded = DeviceStore.Load(path);
            Assert.Equal(T0, loaded.Created);
            Assert.Single(loaded.Devices);
            Assert.Equal("Kitchen", loaded.Get("AA:BB:CC:DD:EE:01")!.Custom);
            Assert.Equal(T0.AddMinutes(2), loaded.Get("AA:BB:CC:DD:EE:01")!.First);
            var text = File.ReadAllText(path);
            Assert.Contains("\"created\": \"2026-11-21T09:00:00Z\"", text);       // pretty, ISO-8601, keyed by MAC
            Assert.True(text.IndexOf("\"custom\"") < text.IndexOf("\"first\"") && text.IndexOf("\"first\"") < text.IndexOf("\"last\""));   // sorted keys
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BrokenFilesGiveAnEmptyStore()
    {
        Assert.Empty(DeviceStore.FromJson("garbage").Devices);
        Assert.Empty(DeviceStore.FromJson("[]").Devices);
        var s = DeviceStore.FromJson("""{"created":"2026-01-01T00:00:00Z","devices":{"nope":{"first":"2026-01-01T00:00:00Z"},"AA:BB:CC:DD:EE:01":{"first":"2026-01-01T00:00:00Z","last":"2026-01-02T00:00:00Z","name":"X"},"AA:BB:CC:DD:EE:02":{"last":"x"}}}""");
        Assert.Single(s.Devices);
        Assert.Equal("X", s.Get("AA:BB:CC:DD:EE:01")!.Name);
        Assert.Empty(DeviceStore.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".json")).Devices);
    }
}

public class NetDevicesBuildTests
{
    static uint Ip(string s) => NetAddress.ParseIp(s)!.Value;
    static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    static string? Vendor(byte[] mac) => mac[0] == 0xB8 ? "Raspberry Pi" : mac[0] == 0x00 ? "Sonos, Inc." : null;

    static ScanState State()
    {
        var s = new ScanState { Info = new ScanInfo(Ip("192.168.1.23"), 24, Ip("192.168.1.1"), [0x3C, 0x7C, 0x3F, 1, 2, 3], "OFFICE-PC") };
        s.Targets = Enumerable.Range(1, 254).Select(i => Ip("192.168.1.0") + (uint)i).Where(a => a != s.Info.Ip).ToHashSet();
        s.ArpDone = true;
        return s;
    }

    [Fact]
    public void BuildsRowsNamesAndKinds()
    {
        var s = State();
        s.Found[Ip("192.168.1.1")] = new FoundHost { Mac = [0x10, 0, 0, 0, 0, 1], Ms = 2 };
        s.Found[Ip("192.168.1.52")] = new FoundHost { Mac = [0x00, 0x0E, 0x58, 1, 2, 3], Ms = 9, Loss = 1 / 3.0 };
        s.Found[Ip("192.168.1.60")] = new FoundHost { Mac = [0xDA, 1, 2, 3, 4, 5], Ms = 30 };
        s.Found[Ip("192.168.1.70")] = new FoundHost { Mac = [0x20, 1, 2, 3, 4, 5] };
        s.Bonjour[Ip("192.168.1.52")] = new BonjourHost { Name = "Living Room", Types = ["_sonos._tcp", "_raop._tcp"] };
        s.Names[Ip("192.168.1.52")] = new() { ["DNS"] = "sonos-lr", ["mDNS"] = "living room" };    // same name as Bonjour, ignoring case
        s.Names[Ip("192.168.1.70")] = new() { ["NetBIOS"] = "NAS01", ["DNS"] = "nas01.lan" };
        var list = NetDevices.Build(s, new DeviceStore(null, Now.AddDays(-9)), Vendor, Now);
        var me = list.Single(d => d.IsMe);
        Assert.Equal("OFFICE-PC", me.Name); Assert.Equal("This PC", me.Names[0].Source); Assert.Equal(DeviceKind.Computer, me.Kind);
        var router = list.Single(d => d.IsRouter);
        Assert.Equal("Router", router.Name); Assert.Equal(DeviceKind.Router, router.Kind);
        var sonos = list.Single(d => d.Ip == Ip("192.168.1.52"));
        Assert.Equal("Living Room", sonos.Name);
        Assert.Equal(new[] { "Bonjour", "DNS" }, sonos.Names.Select(n => n.Source));
        Assert.Equal(DeviceKind.Speaker, sonos.Kind); Assert.Equal(1 / 3.0, sonos.Loss);
        Assert.Equal("Sonos, Inc.", sonos.Vendor);
        var phone = list.Single(d => d.Ip == Ip("192.168.1.60"));
        Assert.True(phone.PrivateMac); Assert.Equal("Unknown device", phone.Name); Assert.Equal(DeviceKind.Phone, phone.Kind); Assert.Equal("Private address", phone.VendorTitle);
        var nas = list.Single(d => d.Ip == Ip("192.168.1.70"));
        Assert.Equal("NAS01", nas.Name); Assert.Equal(DeviceKind.Nas, nas.Kind);
        Assert.Equal(5, list.Count);
    }

    [Fact]
    public void OfflineRowsOnlyForThisNetworkAndWhenReadable()
    {
        var s = State();
        s.Found[Ip("192.168.1.1")] = new FoundHost { Mac = [0x10, 0, 0, 0, 0, 1] };
        var store = new DeviceStore(null, Now.AddDays(-9));
        store.Remember([new NetDevice { Mac = [0xB8, 0x27, 0xEB, 1, 2, 3], Ip = Ip("192.168.1.77"), Names = { ("mDNS", "pi-hole") } }], s.Info!.NetworkKey, Now.AddHours(-30));
        store.Remember([new NetDevice { Mac = [0xB8, 0x27, 0xEB, 9, 9, 9], Ip = Ip("10.9.9.9"), Names = { ("mDNS", "office-nas") } }], "10.9.9.0/24 via 10.9.9.1", Now.AddHours(-30));
        var list = NetDevices.Build(s, store, Vendor, Now);
        var off = list.Where(d => !d.Online).ToList();
        Assert.Single(off);                                                       // the other network's device is not listed
        Assert.Equal("pi-hole", off[0].Name); Assert.Equal("off B8:27:EB:01:02:03", off[0].Id); Assert.Equal("Raspberry Pi", off[0].Vendor);
        Assert.Equal(Now.AddHours(-30), off[0].LastSeen);
        s.CustomRange = true;
        Assert.DoesNotContain(NetDevices.Build(s, store, Vendor, Now), d => !d.Online);   // custom range: no offline rows
        s.CustomRange = false; s.Blocked = true;
        Assert.DoesNotContain(NetDevices.Build(s, store, Vendor, Now), d => !d.Online);   // blocked scan: ARP not trustworthy
        s.Blocked = false; s.ArpDone = false;
        Assert.DoesNotContain(NetDevices.Build(s, store, Vendor, Now), d => !d.Online);
    }

    [Fact]
    public void CustomNameAndNewBadge()
    {
        var s = State();
        var mac = new byte[] { 0xB8, 0x27, 0xEB, 1, 2, 3 };
        s.Found[Ip("192.168.1.77")] = new FoundHost { Mac = mac };
        s.Names[Ip("192.168.1.77")] = new() { ["mDNS"] = "pi" };
        var store = new DeviceStore(null, Now.AddHours(-5));
        store.Remember([new NetDevice { Mac = mac, Ip = Ip("192.168.1.77"), Names = { ("mDNS", "pi") } }], "n", Now.AddHours(-1));
        store.Rename("B8:27:EB:01:02:03", "Garage Pi", Now);
        var d = NetDevices.Build(s, store, Vendor, Now).Single(x => x.Ip == Ip("192.168.1.77"));
        Assert.Equal("Garage Pi", d.Name); Assert.Equal("Your name", d.Names[0].Source); Assert.Equal("Garage Pi", d.Custom);
        Assert.True(d.IsNew);
        Assert.Equal(Now.AddHours(-1), d.FirstSeen);
        Assert.Empty(NetDevices.Build(new ScanState(), store, Vendor, Now));       // no network → no rows
    }

    [Fact]
    public void MeIsLeftOutOfACustomRangeThatDoesNotContainIt()
    {
        var s = State();
        s.IncludeMe = false; s.CustomRange = true;
        s.Found[Ip("10.0.0.5")] = new FoundHost { Ms = 3, ViaTcp = true };
        var list = NetDevices.Build(s, new DeviceStore(null, Now.AddDays(-9)), Vendor, Now);
        Assert.Single(list);
        Assert.True(list[0].Remote); Assert.Null(list[0].Mac); Assert.True(list[0].ViaTcp);
    }
}

public class NetNamesTests
{
    [Fact]
    public void FriendlyNamesFromServices()
    {
        var none = new Dictionary<string, string>();
        Assert.Equal("Woonkamer", NetNames.FriendlyName("A1B2C3D4E5F6@Woonkamer", "_raop._tcp", none));
        Assert.Equal("alex-pc", NetNames.FriendlyName("alex-pc [aa:bb:cc:dd:ee:ff]", "_workstation._tcp", none));
        Assert.Null(NetNames.FriendlyName("0123456789ABCDEF", "_matter._tcp", none));
        Assert.Equal("Kitchen TV", NetNames.FriendlyName("Chromecast-abc123", "_googlecast._tcp", new Dictionary<string, string> { ["fn"] = "Kitchen TV" }));
        Assert.Equal("Brother HL", NetNames.FriendlyName("Brother HL", "_ipp._tcp", none));
        Assert.Null(NetNames.FriendlyName("@", "_raop._tcp", none));
        Assert.Equal(64, NetNames.FriendlyName(new string('n', 200), "_ipp._tcp", none)!.Length);
    }

    [Fact]
    public void ServicePriorityNamesTheDevice()
    {
        var b = new BonjourHost();
        NetNames.AddService(b, "_http._tcp", "web-name", null);
        NetNames.AddService(b, "_airplay._tcp", "AirPlay name", "AppleTV6,2");
        NetNames.AddService(b, "_ssh._tcp", "ssh-name", "other");
        Assert.Equal("AirPlay name", b.Name); Assert.Equal("AppleTV6,2", b.Model);
        NetNames.AddService(b, "_device-info._tcp", "Info name", "Mac14,2");
        Assert.Equal("Info name", b.Name); Assert.Equal("Mac14,2", b.Model);
        Assert.Equal(4, b.Types.Count);
        Assert.Equal("Apple device", NetNames.ServiceTitle("_companion-link._tcp")); Assert.Equal("Printing", NetNames.ServiceTitle("_ipp._tcp"));
        Assert.Equal("HomeKit", NetNames.ServiceTitle("_hap._udp")); Assert.Equal("_unknown._tcp", NetNames.ServiceTitle("_unknown._tcp"));
        Assert.Equal(22, NetNames.ServiceTypes.Length);
    }

    [Fact]
    public void ModelsAndSources()
    {
        Assert.Equal("MacBook Pro", NetNames.AppleModel("MacBookPro18,3")); Assert.Equal("Mac mini", NetNames.AppleModel("Macmini9,1"));
        Assert.Equal("HomePod mini", NetNames.AppleModel("AudioAccessory5,1")); Assert.Equal("HomePod", NetNames.AppleModel("AudioAccessory1,1"));
        Assert.Equal("Apple Vision Pro", NetNames.AppleModel("RealityDevice14,1")); Assert.Null(NetNames.AppleModel("Macintosh")); Assert.Null(NetNames.AppleModel("HP LaserJet"));
        Assert.Equal("MacBook Pro (MacBookPro18,3)", NetNames.ModelText("MacBookPro18,3", null));
        Assert.Equal("MacBook Pro (MacBookPro18,3) · Sonos One by Sonos, Inc.", NetNames.ModelText("MacBookPro18,3", new UpnpInfo(null, "Sonos, Inc.", "Sonos One", null)));
        Assert.Equal("Sonos One", NetNames.ModelText("Sonos One", new UpnpInfo(null, "Sonos, Inc.", "Sonos One", null)));   // not twice
        Assert.Null(NetNames.ModelText(null, null));
        Assert.Equal("model", NetNames.TxtModel(new Dictionary<string, string> { ["model"] = "model", ["am"] = "x" }));
        Assert.Null(NetNames.TxtModel(new Dictionary<string, string> { ["model"] = new string('m', 80) }));
        Assert.Equal("Remembered", NetNames.SourceTitle("Remembered")); Assert.Equal("mDNS", NetNames.SourceTitle("mDNS")); Assert.Equal("This PC", NetNames.SourceTitle("This PC"));
    }

    [Fact]
    public void GuessUsesServicesAndPorts()
    {
        Assert.Equal(DeviceKind.Printer, Devices.Guess("", "", [], false, false, false, ["_ipp._tcp"]));
        Assert.Equal(DeviceKind.Tv, Devices.Guess("", "", [], false, false, false, ["_googlecast._tcp"]));
        Assert.Equal(DeviceKind.Speaker, Devices.Guess("", "", [], false, false, false, ["_raop._tcp"]));
        Assert.Equal(DeviceKind.SmartHome, Devices.Guess("", "", [], false, false, false, ["_matter._tcp"]));
        Assert.Equal(DeviceKind.Computer, Devices.Guess("", "", [], false, false, false, ["_smb._tcp"]));
        Assert.Equal(15, Devices.Ports.Length);
        Assert.Equal("http://10.0.0.5:32400/web", Devices.WebUrl(NetAddress.ParseIp("10.0.0.5")!.Value, 32400));
        Assert.Equal("Camera stream", Devices.PortName(554)); Assert.Equal("Port 7", Devices.PortName(7));
    }
}

public class NetPacketTests
{
    // ---- tiny DNS builder for crafted replies ----
    static byte[] Name(string n) => n.Split('.', StringSplitOptions.RemoveEmptyEntries).SelectMany(l => new[] { (byte)l.Length }.Concat(Encoding.ASCII.GetBytes(l))).Append((byte)0).ToArray();
    static byte[] Rr(string owner, ushort type, byte[] data) => [.. Name(owner), (byte)(type >> 8), (byte)type, 0x80, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length, .. data];
    static byte[] Response(int answers, int additional, params byte[][] rrs) => [0, 0, 0x84, 0, 0, 0, 0, (byte)answers, 0, 0, 0, (byte)additional, .. rrs.SelectMany(x => x)];
    static byte[] Txt(params string[] kv) => kv.SelectMany(s => new[] { (byte)s.Length }.Concat(Encoding.ASCII.GetBytes(s))).ToArray();

    [Fact]
    public void BuildsQueriesAndReadsNames()
    {
        var q = NetPackets.DnsQuery("_airplay._tcp.local", 12, 7);
        Assert.Equal(new byte[] { 0, 7, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, q.Take(12));
        Assert.Equal(new byte[] { 0, 12, 0x80, 1 }, q.TakeLast(4));                    // PTR, QU bit, IN
        int off = 12;
        Assert.Equal("_airplay._tcp.local", NetPackets.ReadName(q, ref off));
        Assert.Equal(q.Length - 4, off);
        Assert.Equal("42.1.168.192.in-addr.arpa", NetPackets.ReverseName(NetAddress.ParseIp("192.168.1.42")!.Value));
        Assert.Equal(0, NetPackets.DnsQuery("a.b", 1, unicast: false)[^2]);
        // compression pointer loop must not hang
        byte[] loop = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xC0, 12];
        int o = 12;
        Assert.Null(NetPackets.ReadName(loop, ref o));
        o = 3; Assert.Null(NetPackets.ReadName([1, 2, 3, 63], ref o));                 // label runs past the end
    }

    [Fact]
    public void ReadsReverseAnswers()
    {
        var resp = Response(1, 0, Rr("42.1.168.192.in-addr.arpa", 12, Name("alex-iphone.local")));
        var r = NetPackets.ReverseAnswers(resp);
        Assert.Equal("alex-iphone", r[NetAddress.ParseIp("192.168.1.42")!.Value]);
        Assert.Empty(NetPackets.ReverseAnswers(Response(1, 0, Rr("evil.example.com", 12, Name("x.local")))));
        Assert.Empty(NetPackets.ReverseAnswers([1, 2, 3]));
        Assert.Empty(NetPackets.ReverseAnswers(new byte[10_000]));                      // too big
        Assert.Empty(NetPackets.ReverseAnswers(Response(1, 0, Rr("1.1.1.10.in-addr.arpa", 12, Name("192.168.1.9")))));   // a bare IP is no name
    }

    [Fact]
    public void ReadsMdnsServicesWithTxt()
    {
        var inst = Name("Living Room._airplay._tcp.local");
        var resp = Response(1, 1, Rr("_airplay._tcp.local", 12, inst), Rr("Living Room._airplay._tcp.local", 16, Txt("model=AppleTV6,2", "flags=0x4")));
        var s = NetPackets.MdnsServices(resp);
        Assert.Single(s);
        Assert.Equal(("_airplay._tcp", "Living Room"), (s[0].Type, s[0].Instance));
        Assert.Equal("AppleTV6,2", s[0].Txt["model"]);
        Assert.Empty(NetPackets.MdnsServices(Response(1, 0, Rr("_notours._tcp.local", 12, Name("x._notours._tcp.local")))));
        Assert.Empty(NetPackets.MdnsServices(Response(1, 0, Rr("_ipp._tcp.local", 12, Name("x._other._tcp.local")))));   // instance must belong to the type
    }

    [Fact]
    public void NetbiosQueryAndAnswer()
    {
        var q = NetPackets.NetbiosQuery();
        Assert.Equal(50, q.Length); Assert.Equal(0x21, q[^3]);                          // NBSTAT
        byte[] Name15(string n, byte suffix, byte flags) => [.. Encoding.ASCII.GetBytes(n.PadRight(15)), suffix, flags, 0];
        byte[] answer(params byte[][] names) => [0x46, 0x42, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, .. Name("*"), 0, 0x21, 0, 1, 0, 0, 0, 0, 0, 0, (byte)names.Length, .. names.SelectMany(x => x)];
        Assert.Equal("OFFICE-PC", NetPackets.NetbiosName(answer(Name15("OFFICE-PC", 0, 0x04))));
        Assert.Equal("NAS01", NetPackets.NetbiosName(answer(Name15("WORKGROUP", 0, 0x84), Name15("NAS01", 0, 0x04))));   // group names are skipped
        Assert.Null(NetPackets.NetbiosName(answer(Name15("NAS01", 0x20, 0x04))));      // not the computer name suffix
        Assert.Null(NetPackets.NetbiosName(answer(Name15("\u0001A", 0, 0x04))));      // control characters are rejected
        Assert.Null(NetPackets.NetbiosName([1, 2, 3])); Assert.Null(NetPackets.NetbiosName(new byte[3000]));
    }

    [Fact]
    public void SsdpAndUpnp()
    {
        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLocation: http://192.168.1.52:1400/xml/device_description.xml\r\nST: upnp:rootdevice\r\n\r\n");
        var h = NetPackets.SsdpHeaders(reply);
        Assert.Equal("http://192.168.1.52:1400/xml/device_description.xml", h["location"]);
        Assert.Empty(NetPackets.SsdpHeaders(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nLocation: x\r\n")));
        Assert.Empty(NetPackets.SsdpHeaders(new byte[5000]));
        uint ip = NetAddress.ParseIp("192.168.1.52")!.Value;
        Assert.NotNull(NetPackets.LanUrl(h["location"], ip));
        Assert.Null(NetPackets.LanUrl(h["location"], NetAddress.ParseIp("192.168.1.53")!.Value));          // must be the sender's address
        Assert.Null(NetPackets.LanUrl("https://192.168.1.52/x", ip));                                       // http only
        Assert.Null(NetPackets.LanUrl("http://8.8.8.8/x", NetAddress.ParseIp("8.8.8.8")!.Value));           // private LAN only
        Assert.Null(NetPackets.LanUrl("http://router.local/x", ip));
        Assert.Null(NetPackets.LanUrl("http://192.168.1.52/" + new string('a', 400), ip));

        var xml = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><deviceType>urn:schemas-upnp-org:device:ZonePlayer:1</deviceType><friendlyName>Living Room</friendlyName><manufacturer>Sonos, Inc.</manufacturer><modelName>Sonos One</modelName><deviceList><device><friendlyName>Inner</friendlyName></device></deviceList></device></root>";
        var u = NetPackets.ParseUpnp(Encoding.UTF8.GetBytes(xml))!;
        Assert.Equal(new UpnpInfo("Living Room", "Sonos, Inc.", "Sonos One", "urn:schemas-upnp-org:device:ZonePlayer:1"), u);      // first occurrence = root device
        Assert.Null(NetPackets.ParseUpnp(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a \"b\">]><root><friendlyName>&a;</friendlyName></root>")));
        Assert.Null(NetPackets.ParseUpnp(Encoding.UTF8.GetBytes("<!-- " + new string('x', 3000) + " --><!DOCTYPE x><root/>")));      // late DOCTYPE found too
        Assert.Null(NetPackets.ParseUpnp([0xFF, 0xFE, 0x3C, 0]));
        Assert.Null(NetPackets.ParseUpnp(new byte[70_000]));
        Assert.Null(NetPackets.ParseUpnp(Encoding.UTF8.GetBytes("<root><other>1</other></root>")));
        Assert.Null(NetPackets.ParseUpnp(Encoding.UTF8.GetBytes("<root><friendlyName>" + new string('n', 101) + "</friendlyName></root>")));   // values over 100 characters are dropped
        Assert.Null(NetPackets.ParseUpnp(Encoding.UTF8.GetBytes("not xml at all")));
    }

    [Fact]
    public void WakeOnLanMagicPacket()
    {
        var mac = NetAddress.ParseMac("B8:27:EB:01:02:03")!;
        var p = NetPackets.MagicPacket(mac)!;
        Assert.Equal(102, p.Length);
        Assert.All(p.Take(6), b => Assert.Equal(0xFF, b));
        for (int i = 0; i < 16; i++) Assert.Equal(mac, p.Skip(6 + i * 6).Take(6));
        Assert.Null(NetPackets.MagicPacket([1, 2, 3]));
    }
}
