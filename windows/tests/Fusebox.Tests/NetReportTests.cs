using Fusebox.Core.Checkup;
using Fusebox.Core.Network;

public class NetReportTests
{
    static readonly ScrubIdentity Id = new("jdoe1", "Jane Doe", "JANES-LAPTOP", "CONTOSO");
    static readonly DateTime Now = new(2026, 3, 4, 9, 5, 0);

    [Fact]
    public void Full_report_has_all_sections_and_note()
    {
        var t = NetReport.Build(new NetReportInput(Now, "Wi-Fi", Vpn: false, ProfileCategory: 1, EncryptedDns: true, Metered: false,
            Rssi: -72, LinkMbps: 433, Channel: 36, Band: "5 GHz", WidthMhz: 80, Standard: "Wi-Fi 5 (802.11ac)",
            Router: PingStats.From([2, 3, 2.5], 10), Internet: PingStats.From([14, 15], 10), DnsMs: 22, Ipv6Internet: false,
            Finding: new Finding(FindCode.WeakSignal, "Weak signal", false, "router 3 ms"),
            Speed: new SpeedEntry(DateTime.UtcNow, 95, 20, 12, SpeedKind.Full, 110, 22, 80, 0.3), Devices: 14), Id);
        foreach (var s in new[] { "CONNECTION", "WI-FI", "QUALITY", "FIND THE PROBLEM", "SPEED TEST", "NETWORK", "-72 dBm (weak)", "Channel width: 80 MHz", "Bufferbloat: some (+68 ms)", "Stability: unsteady", "Devices found: 14", "5 GHz" })
            Assert.Contains(s, t);
        Assert.EndsWith(NetReport.Note + Environment.NewLine, t);
    }

    [Fact]
    public void Empty_sections_are_left_out_and_missing_data_is_said()
    {
        var t = NetReport.Build(new NetReportInput(Now, "Ethernet"), Id);
        Assert.DoesNotContain("WI-FI", t); Assert.DoesNotContain("SPEED TEST", t); Assert.DoesNotContain("FIND THE PROBLEM", t); Assert.DoesNotContain("Devices found", t);
        Assert.Contains("Router: not measured", t);
    }

    [Fact]
    public void Hostile_input_is_scrubbed()
    {
        var f = new Finding(FindCode.AllGood, "ok on JANES-LAPTOP via 192.168.1.1 aa:bb:cc:dd:ee:ff", true, "router 192.168.0.1, user jdoe1");
        var t = NetReport.Build(new NetReportInput(Now, "Wi-Fi", Standard: "10.0.0.7 CONTOSO", Finding: f), Id);
        foreach (var bad in new[] { "192.168", "10.0.0.7", "aa:bb", "JANES", "jdoe1", "CONTOSO", "Jane" }) Assert.DoesNotContain(bad, t);
    }
}
