using Fusebox.Core.Network;

public class NetAddressTests
{
    static uint Ip(string s) => NetAddress.ParseIp(s)!.Value;

    [Fact]
    public void ParsesIpAndMac()
    {
        Assert.Equal("192.168.1.23", NetAddress.IpString(Ip("192.168.1.23")));
        foreach (var bad in new[] { "1.2.3", "1.2.3.256", "1..2.3", "a.1.2.3.4", "", "1.2.3.4.5" }) Assert.Null(NetAddress.ParseIp(bad));
        Assert.Equal(new byte[] { 0xB8, 0x27, 0xEB, 1, 2, 3 }, NetAddress.ParseMac("B8:27:EB:01:02:03"));
        Assert.Null(NetAddress.ParseMac("B8:27"));
        Assert.Equal("0A:0B:0C:0D:0E:0F", NetAddress.MacString(NetAddress.ParseMac("0a-0b-0c-0d-0e-0f")!));
    }

    [Fact]
    public void HostsOfOwnSubnet()
    {
        var h24 = NetAddress.Hosts(Ip("192.168.1.23"), 24);
        Assert.Equal(254, h24.Count);
        Assert.Equal(Ip("192.168.1.1"), h24[0]);
        Assert.Equal(Ip("192.168.1.254"), h24[^1]);
        Assert.Equal(1022, NetAddress.Hosts(Ip("10.0.5.9"), 22).Count);          // /22 = the cap
        Assert.Equal(1022, NetAddress.Hosts(Ip("10.0.5.9"), 16).Count);          // bigger → /22 around us
        Assert.Equal(Ip("10.0.4.1"), NetAddress.Hosts(Ip("10.0.5.9"), 16)[0]);
        Assert.Equal(14, NetAddress.Hosts(Ip("192.168.1.5"), 28).Count);
        Assert.Empty(NetAddress.Hosts(Ip("192.168.1.5"), 32));
        Assert.Empty(NetAddress.Hosts(Ip("192.168.1.5"), 31));
    }

    [Fact]
    public void ParsesRanges()
    {
        var r = NetAddress.ParseRange("192.168.1.0/24");
        Assert.True(r.Ok); Assert.Equal(Ip("192.168.1.1"), r.First); Assert.Equal(Ip("192.168.1.254"), r.Last); Assert.Equal(254, r.Count);
        Assert.Equal(1022, NetAddress.ParseRange("10.0.1.77/22").Count);                 // not aligned
        Assert.Equal(Ip("10.0.0.1"), NetAddress.ParseRange("10.0.1.77/22").First);
        Assert.Equal(2, NetAddress.ParseRange("192.168.1.6/31").Count);
        var one = NetAddress.ParseRange("192.168.1.7/32");
        Assert.True(one.Ok); Assert.Equal(one.First, one.Last);
        var dash = NetAddress.ParseRange("192.168.1.10-192.168.1.80");
        Assert.Equal((Ip("192.168.1.10"), Ip("192.168.1.80")), (dash.First, dash.Last));
        Assert.Equal(NetAddress.ParseRange("192.168.1.10-80"), dash);
        Assert.Equal(NetAddress.ParseRange(" 192.168.1.10 - 80 "), dash);
        Assert.Equal(1, NetAddress.ParseRange("192.168.1.7").Count);
        foreach (var bad in new[] { "", "abc", "192.168.1", "192.168.1.300", "192.168.1.10-300", "192.168.1.10-", "192.168.1.1-2-3", "192.168.1.0/33", "192.168.1.0/", "192.168.1.10-+80" })
            Assert.Equal(RangeError.Invalid, NetAddress.ParseRange(bad).Error);
        Assert.Equal(RangeError.Reversed, NetAddress.ParseRange("192.168.1.80-10").Error);
        Assert.Equal(RangeError.Reversed, NetAddress.ParseRange("192.168.1.80-192.168.1.10").Error);
    }

    [Fact]
    public void CapsRangesAt1024()
    {
        var big = NetAddress.ParseRange("10.0.0.0/16");
        Assert.Equal(RangeError.TooLarge, big.Error);
        Assert.Equal(65_534, big.Count);
        Assert.Equal(RangeError.TooLarge, NetAddress.ParseRange("10.0.0.0-10.0.4.1").Error);
        Assert.True(NetAddress.ParseRange("10.0.0.0-10.0.3.255").Ok);   // exactly 1024
        Assert.Equal(1022, NetAddress.ParseRange("10.0.0.0/22").Count);
        Assert.Equal(RangeError.TooLarge, NetAddress.ParseRange("10.0.0.0/21").Error);
    }

    [Fact]
    public void LanAddresses()
    {
        Assert.True(NetAddress.IsLan(Ip("192.168.1.1")) && NetAddress.IsLan(Ip("10.1.2.3")) && NetAddress.IsLan(Ip("172.20.0.1")));
        Assert.False(NetAddress.IsLan(Ip("8.8.8.8")) || NetAddress.IsLan(Ip("172.32.0.1")));
    }
}

public class DevicesTests
{
    [Fact]
    public void ParsesOuiTsv()
    {
        var t = Devices.ParseOui("000000\tXerox\n00000C\tCisco Systems\r\n001132\tSynology\nFCFCFC\tNETGEAR\nzzzzzz\tbad\n00AABB\tIEEE Registration Authority\n12345\ttoo short\n");
        Assert.Equal(4, t.Count);
        Assert.Equal("Cisco Systems", t[0x00000C]);
        Assert.Equal("Netgear", t[0xFCFCFC]);
        Assert.False(t.ContainsKey(0x00AABB));
    }

    [Fact]
    public void TidiesVendorNames()
    {
        Assert.Equal("TP-LINK", Devices.TidyVendor("TP-LINK"));
        Assert.Equal("Huawei Technologies", Devices.TidyVendor("HUAWEI TECHNOLOGIES"));
        Assert.Equal("LG", Devices.TidyVendor("LG"));
        Assert.Null(Devices.TidyVendor("Private"));
    }

    [Fact]
    public void LooksUpTheEmbeddedIeeeList()
    {
        Assert.True(Devices.Table.Count > 30_000);
        Assert.StartsWith("Raspberry Pi", Devices.Vendor([0xB8, 0x27, 0xEB, 1, 2, 3]));
        Assert.Null(Devices.Vendor([0x12, 0x34, 0x56, 0, 0, 0]));
        Assert.Null(Devices.Vendor([0x12]));
    }

    [Fact]
    public void DetectsPrivateMacs()
    {
        Assert.True(NetAddress.IsPrivateMac([0xDA, 0, 0, 0, 0, 0]));
        Assert.True(NetAddress.IsPrivateMac([0x02, 0, 0, 0, 0, 0]));
        Assert.False(NetAddress.IsPrivateMac([0xB8, 0x27, 0xEB, 0, 0, 0]));
    }

    [Fact]
    public void GuessesDeviceType()
    {
        DeviceKind G(string name = "", string vendor = "", ushort[]? ports = null, bool router = false, bool me = false, bool priv = false) =>
            Devices.Guess(name, vendor, ports ?? [], router, me, priv);
        Assert.Equal(DeviceKind.Router, G(router: true, vendor: "HP"));
        Assert.Equal(DeviceKind.Computer, G(me: true));
        Assert.Equal(DeviceKind.Printer, G(ports: [631]));
        Assert.Equal(DeviceKind.Printer, G(vendor: "Brother Industries"));
        Assert.Equal(DeviceKind.Console, G(name: "Xbox-One"));
        Assert.Equal(DeviceKind.Phone, G(name: "Alex-iPhone"));
        Assert.Equal(DeviceKind.Tv, G(ports: [8009]));
        Assert.Equal(DeviceKind.Speaker, G(vendor: "Sonos, Inc."));
        Assert.Equal(DeviceKind.SmartHome, G(vendor: "Philips Lighting"));
        Assert.Equal(DeviceKind.Nas, G(vendor: "Synology"));
        Assert.Equal(DeviceKind.Computer, G(ports: [445]));
        Assert.Equal(DeviceKind.Router, G(vendor: "TP-LINK Technologies"));
        Assert.Equal(DeviceKind.Phone, G(priv: true));
        Assert.Equal(DeviceKind.Unknown, G(vendor: "Zzz"));
    }

    [Fact]
    public void WebUrlsAndSearch()
    {
        uint ip = NetAddress.ParseIp("192.168.1.40")!.Value;
        Assert.Equal("http://192.168.1.40/", Devices.WebUrl(ip, 80));
        Assert.Equal("https://192.168.1.40:5001/", Devices.WebUrl(ip, 5001));
        Assert.Null(Devices.WebUrl(ip, 22));
        Assert.Equal("alex", Devices.CleanHostname("alex.fritz.box"));
        Assert.Null(Devices.CleanHostname("192.168.1.5"));
        foreach (var q in new[] { "", "  ", "living", "ALEX-TV", "168.1.42", "b8:27:eb", "raspberry" })
            Assert.True(Devices.Matches(q, "Living room ALEX-TV", "Raspberry Pi", "192.168.1.42", "B8:27:EB:01:02:03"), q);
        Assert.False(Devices.Matches("sonos", "Living room", "Raspberry Pi", "192.168.1.42", "B8:27:EB:01:02:03"));
    }
}

public class QualityTests
{
    static PingStats Stats(double v, int got, int sent = 10) => PingStats.From(Enumerable.Repeat(v, got).ToList(), sent);

    [Fact]
    public void PingStatsAndJitter()
    {
        var s = PingStats.From([10, 20, 10, 20], 5);
        Assert.Equal(4, s.Got); Assert.Equal(15, s.Avg); Assert.Equal(10, s.Min); Assert.Equal(20, s.Max);
        Assert.Equal(10, s.Jitter); Assert.Equal(0.2, s.Loss, 6);
        Assert.Null(PingStats.From([], 3).Avg);
        Assert.Null(PingStats.From([7], 1).Jitter);
    }

    [Fact]
    public void SpeedMath_Basics()
    {
        Assert.Equal(80, SpeedMath.Mbps(10_000_000, 1), 6);          // 10 MB in 1 s = 80 Mbit/s
        Assert.Equal(40, SpeedMath.Mbps(10_000_000, 2), 6);
        Assert.Equal(0, SpeedMath.Mbps(5, 0));
        Assert.Equal([3.0, 4, 5], SpeedMath.Trimmed([1, 2, 3, 4, 5], 2));
        Assert.Equal([7.0], SpeedMath.Trimmed([7], 3));
        Assert.Equal(3, SpeedMath.Median([5, 1, 3]));
        Assert.Equal(2.5, SpeedMath.Median([4, 1, 3, 2]));
        Assert.Null(SpeedMath.Median([]));
        Assert.Equal(0, SpeedMath.Variation([100, 100, 100]));
        Assert.Null(SpeedMath.Variation([5]));
        Assert.Equal(0.1, SpeedMath.Variation([90, 110])!.Value, 9);
        var run = SpeedMath.Trimmed([40, 220, 480, 510, 495, 620, 505], 2);   // slow ramp-up, then ~500 with one peak
        Assert.Equal(505, SpeedMath.Median(run)); Assert.Equal(620, run.Max()); Assert.True(SpeedMath.Mean(run) > 480);
    }

    [Fact]
    public void BufferbloatVerdicts()
    {
        Assert.True(SpeedMath.LoadVerdict(12, 20).Ok);
        Assert.Null(SpeedMath.LoadVerdict(12, 70).Ok);
        Assert.False(SpeedMath.LoadVerdict(12, 300).Ok);
    }

    [Fact]
    public void WifiMaths()
    {
        Assert.Equal((6, "2.4 GHz"), (WifiMath.Channel(2_437_000)!.Value, WifiMath.Band(2_437_000)));
        Assert.Equal(14, WifiMath.Channel(2_484_000));
        Assert.Equal((36, "5 GHz"), (WifiMath.Channel(5_180_000)!.Value, WifiMath.Band(5_180_000)));
        Assert.Equal((1, "6 GHz"), (WifiMath.Channel(5_955_000)!.Value, WifiMath.Band(5_955_000)));
        Assert.Null(WifiMath.Band(1_000_000));
        Assert.Equal("Wi-Fi 6 (802.11ax)", WifiMath.Phy(10));
        Assert.Equal(-50, WifiMath.QualityToRssi(100)); Assert.Equal(-100, WifiMath.QualityToRssi(0)); Assert.Equal(-75, WifiMath.QualityToRssi(50));
    }

    static DiagInput In(bool wired = false, int? rssi = -55, PingStats? router = null, PingStats? internet = null, double? dns = 20, int? snr = 35, string? band = "5 GHz", bool? https = true, string? ip = "192.168.1.9") =>
        new(true, ip, true, router ?? Stats(3, 10), internet ?? Stats(15, 10), dns, https, wired, wired ? null : rssi, wired ? null : snr, wired ? null : band, wired ? null : 36);

    [Fact]
    public void FindsTheProblem()
    {
        var ok = Diagnosis.Find(In());
        Assert.Equal((FindCode.AllGood, true), (ok.Code, ok.Ok));
        Assert.Equal("router 3 ms, internet 15 ms, DNS 20 ms, Wi-Fi -55 dBm · SNR 35 dB · ch 36 (5 GHz)", ok.Numbers);
        Assert.Equal(FindCode.ProviderProblem, Diagnosis.Find(In(internet: Stats(180, 10))).Code);
        Assert.Equal(FindCode.ProviderProblem, Diagnosis.Find(In(wired: true, internet: Stats(15, 8))).Code);          // 20% loss
        var lossy = Stats(4, 6);
        Assert.Equal(FindCode.WeakSignal, Diagnosis.Find(In(rssi: -80, snr: 12, router: lossy)).Code);
        Assert.Equal(FindCode.WifiUnstable, Diagnosis.Find(In(band: "2.4 GHz", router: lossy)).Code);
        Assert.Equal(FindCode.CableUnstable, Diagnosis.Find(In(wired: true, router: lossy)).Code);
        Assert.Equal(FindCode.RouterDown, Diagnosis.Find(In(wired: true, router: Stats(0, 0), internet: Stats(0, 0))).Code);
        Assert.Equal(FindCode.WeakSignal, Diagnosis.Find(In(rssi: -80, router: Stats(0, 0), internet: Stats(0, 0))).Code);
        Assert.Equal(FindCode.InternetDown, Diagnosis.Find(In(internet: Stats(0, 0))).Code);
        Assert.Equal(FindCode.DnsFail, Diagnosis.Find(In(dns: null)).Code);
        Assert.Equal(FindCode.DnsSlow, Diagnosis.Find(In(dns: 400)).Code);
        var weak = Diagnosis.Find(In(rssi: -75));
        Assert.Equal((FindCode.WeakButWorks, (bool?)null), (weak.Code, weak.Ok));
        var noRouter = In() with { Router = null, Internet = Stats(0, 0) };
        Assert.Equal(FindCode.NoRouter, Diagnosis.Find(noRouter).Code);
        Assert.Equal(FindCode.NotConnected, Diagnosis.Find(In(ip: null)).Code);
        Assert.Equal(FindCode.NotConnected, Diagnosis.Find(In() with { AdapterUp = false }).Code);
    }

    [Fact]
    public void StepsWithAdvice()
    {
        var good = Diagnosis.Steps(In());
        Assert.Equal(6, good.Count);
        Assert.All(good, s => { Assert.True(s.Ok); Assert.Null(s.Advice); });

        var apipa = Diagnosis.Steps(In(ip: "169.254.3.4"));
        Assert.Equal([true, false], apipa.Take(2).Select(s => s.Ok).ToArray());
        Assert.NotNull(apipa[1].Advice);
        Assert.All(apipa.Skip(2), s => Assert.Null(s.Ok));                  // later steps not tested

        var dnsDown = Diagnosis.Steps(In(dns: null));
        Assert.Equal(false, dnsDown[3].Ok); Assert.Contains("1.1.1.1", dnsDown[3].Advice);

        var down = Diagnosis.Steps(In(internet: Stats(0, 0), https: false));
        Assert.Equal([true, true, true, true, false, false], down.Select(s => s.Ok).ToArray());
        Assert.NotNull(down[4].Advice);

        var off = Diagnosis.Steps(In() with { AdapterUp = false });
        Assert.False(off[0].Ok);
        Assert.All(off.Skip(1), s => Assert.Null(s.Ok));
    }

    [Fact]
    public void ParsesCloudflareMeta()
    {
        var m = PublicIpParser.Parse("""{"clientIp":"203.0.113.7","city":"Utrecht","country":"NL","colo":"AMS","asOrganization":"Example ISP"}""")!;
        Assert.Equal(("203.0.113.7", "Example ISP", "Utrecht, NL (Cloudflare AMS)"), (m.Ip, m.Isp, m.Place));
        Assert.Null(PublicIpParser.Parse("nope"));
        Assert.Null(PublicIpParser.Parse("{}"));
    }
}
