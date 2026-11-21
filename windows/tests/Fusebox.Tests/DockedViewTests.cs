using Fusebox.Core.Docked;

public class DockedViewTests
{
    static UsbNode Dev(string name, int depth, string path, long bps = 480_000_000, long max = 0, bool hub = false, bool? connectable = null, int? vid = null, int cls = -1, bool? typeC = null) =>
        new(name, null, depth, hub, null, null, max > bps, typeC, bps, max == 0 ? bps : max, vid, 1, cls, null, connectable, path, 0);

    static DockedSnapshot Snap(IReadOnlyList<UsbNode> usb, PowerInfo? p = null, IReadOnlyList<DisplayInfo>? disp = null, IReadOnlyList<BtDevice>? bt = null,
        IReadOnlyList<AudioEndpoint>? audio = null, UplinkInfo? up = null) =>
        new(p ?? new PowerInfo(true, 80, false, true), usb, disp ?? [], bt, audio ?? [], up);

    [Fact]
    public void DockTreeHasConnectorsAndHidesHubsUnlessAsked()
    {
        var usb = new[]
        {
            Dev("Generic USB Hub", 0, "0/1", 5_000_000_000, hub: true, connectable: true, typeC: true),
            Dev("Keyboard", 1, "0/1/1", 12_000_000),
            Dev("SSD", 1, "0/1/2", 5_000_000_000),
            Dev("Webcam", 0, "0/2", 480_000_000, connectable: false),
        };
        var v = DockedBuilder.Build(Snap(usb), new DockedOptions());
        var rows = v.Ports.Single().Rows;
        Assert.Equal(new[] { "USB-C port 1", "Hub", "Keyboard", "SSD" }, rows.Select(r => r.Title));
        Assert.Empty(rows[0].Cols);
        Assert.Equal(new[] { Conn.Elbow }, rows[1].Cols);
        Assert.Equal(new[] { Conn.None, Conn.Tee }, rows[2].Cols);
        Assert.Equal(new[] { Conn.None, Conn.Elbow }, rows[3].Cols);
        Assert.Equal("Webcam", v.Other!.Rows.Single().Title);
        Assert.Equal("5", rows[0].Right!.Big);
        Assert.Equal("Gbit/s", rows[0].Right!.Suffix);
        Assert.Contains(v.Header.Chips, c => c.Label == "devices" && c.Value == "2");
        Assert.DoesNotContain(rows, r => r.Dim);   // hubs only with internals
    }

    [Fact]
    public void InternalsShowHubChainAsOneRow()
    {
        var usb = new[]
        {
            Dev("Dock", 0, "0/1", hub: true, connectable: true),
            Dev("Hub A", 1, "0/1/1", hub: true),
            Dev("Hub B", 2, "0/1/1/1", hub: true),
            Dev("Mouse", 3, "0/1/1/1/1"),
            Dev("Empty hub", 1, "0/1/2", hub: true),
        };
        var rows = DockedBuilder.Build(Snap(usb), new DockedOptions(Internals: true)).Ports.Single().Rows;
        Assert.Contains(rows, r => r.Title == "Hub A" && r.Dim && r.Facts.Contains("2 hubs"));
        Assert.DoesNotContain(rows, r => r.Title == "Empty hub");
        Assert.Contains(rows, r => r.Title == "Mouse");
    }

    [Fact]
    public void SlowDeviceGetsWarningAndNowOfMax()
    {
        var usb = new[] { Dev("Dock", 0, "0/1", hub: true, connectable: true), Dev("T7", 1, "0/1/1", 480_000_000, 10_000_000_000) };
        var row = DockedBuilder.Build(Snap(usb), new DockedOptions()).Ports.Single().Rows.Single(r => r.Title == "T7");
        Assert.True(row.Right!.Slow);
        Assert.Equal("480", row.Right.Big);
        Assert.Equal("Mbit/s of 10 Gbit/s", row.Right.Suffix);
        Assert.Equal("Runs at USB 2 speed, but it can do USB 3. Try another cable or port.", row.Warning);
    }

    [Fact]
    public void NoPortFlagMeansEveryRootIsAPort()
    {
        var v = DockedBuilder.Build(Snap([Dev("A", 0, "0/1"), Dev("B", 0, "0/2")]), new DockedOptions());
        Assert.Equal(2, v.Ports.Count);
        Assert.Null(v.Other);
    }

    [Fact]
    public void SignatureChangesOnlyWithContent()
    {
        var a = DockedBuilder.Build(Snap([Dev("A", 0, "0/1")]), new DockedOptions());
        var b = DockedBuilder.Build(Snap([Dev("A", 0, "0/1")]), new DockedOptions());
        var c = DockedBuilder.Build(Snap([Dev("A", 0, "0/1", 5_000_000_000)]), new DockedOptions());
        Assert.Equal(a.Signature, b.Signature);
        Assert.NotEqual(a.Signature, c.Signature);
    }

    [Fact]
    public void HeaderShowsWattsOnlyWhenWindowsReportsThem()
    {
        var charging = new PowerInfo(true, 82, true, true, 61.4, 65);
        var h = DockedBuilder.Build(Snap([], charging), new DockedOptions()).Header;
        Assert.Equal("61.4", h.Big); Assert.Equal("W", h.Unit); Assert.Equal("into this PC", h.Caption);
        Assert.Equal("charging · full in 1h 5m", h.BatteryText); Assert.Equal("82", h.Percent);

        var noRate = DockedBuilder.Build(Snap([], new PowerInfo(true, 82, true, true)), new DockedOptions()).Header;
        Assert.Equal("Plugged in", noRate.Big); Assert.Null(noRate.Unit);

        Assert.Equal("battery full", DockedText.BatteryText(new PowerInfo(true, 100, false, true)));
        Assert.Equal("not charging", DockedText.BatteryText(new PowerInfo(true, 70, false, true)));
        Assert.Equal("2h 10m left", DockedText.BatteryText(new PowerInfo(false, 70, false, true, null, 130)));
        Assert.Equal("on battery", DockedText.BatteryText(new PowerInfo(false, 70, false, true)));
    }

    [Fact]
    public void NumberFormats()
    {
        static string Rv(RightValue r) => $"{r.Big} {r.Suffix}";
        Assert.Equal("17", DockedText.Trim(16.97)); Assert.Equal("17.5", DockedText.Trim(17.53));
        Assert.Equal("10 / 40 Gbit/s", Rv(DockedText.Link(10_000_000_000, 40_000_000_000, false, false)));
        Assert.Equal("480 Mbit/s of 10 Gbit/s", Rv(DockedText.Link(480_000_000, 10_000_000_000, true, false)));
        Assert.Equal("1.3 GB/s", DockedText.Speed(10_000_000_000, true));
        Assert.Equal("4K60", DockedText.ModeName(3840, 2160, 60));
        Assert.Equal("2000 × 1000 at 75 Hz", DockedText.ModeName(2000, 1000, 75));
        Assert.Equal("60 / 144 Hz", Rv(DockedText.Hertz(60, 144)));
        Assert.Equal("120 Hz", Rv(DockedText.Hertz(120, 120)));
    }

    [Fact]
    public void DisplayWarningsAndDisplayLinkPlacement()
    {
        var slow = new DisplayInfo("Dell", "HDMI", 3840, 2160, 30, null, 5, 3840, 2160, 60, 150, 8);
        Assert.Equal("Runs at 30 Hz but can do 60 Hz. The cable, adapter or port may limit 4K60.", DockedBuilder.DisplayWarning(slow));
        var soft = new DisplayInfo("Dell", "HDMI", 1920, 1080, 60, null, 5, 3840, 2160, 60);
        Assert.Contains("text looks soft", DockedBuilder.DisplayWarning(soft));
        Assert.Null(DockedBuilder.DisplayWarning(new DisplayInfo("X", "HDMI", 1920, 1080, 60, null, 5, 1920, 1080, 60)));

        var usb = new[] { Dev("Dock", 0, "0/1", hub: true, connectable: true), Dev("DisplayLink Dock", 1, "0/1/1", vid: 0x17E9) };
        var disp = new[]
        {
            new DisplayInfo("Mon", "DisplayLink", 1920, 1080, 60, null, 16),
            new DisplayInfo("Other", "HDMI", 1920, 1080, 60, null, 5),
            new DisplayInfo("Panel", "Built-in", 1920, 1080, 60, null, 0x80000000),
        };
        var v = DockedBuilder.Build(Snap(usb, disp: disp), new DockedOptions());
        var link = v.Ports[0].Rows.Single(r => r.Title == "DisplayLink Dock");
        Assert.Equal("1 display via DisplayLink", link.Facts[0]);
        Assert.Contains(v.Ports[0].Rows, r => r.Title == "Mon" && r.Cols.Count == 3);
        Assert.Equal("Displays", v.Ports[1].Rows[0].Title);       // the HDMI one is not linked to a port
        var chip = v.Header.Chips.Single(c => c.Label == "displays");
        Assert.Equal("2", chip.Value);                             // built-in is not counted
        Assert.Contains("Built-in: 1920 × 1080 · 60 Hz", chip.Tip);
        Assert.DoesNotContain(v.AllRows, r => r.Title == "Panel");
        Assert.DoesNotContain(DockedBuilder.Build(Snap(usb, disp: disp), new DockedOptions(Displays: false)).AllRows, r => r.Title == "Mon");
    }

    [Fact]
    public void BluetoothRowsAndBatteryWarning()
    {
        var bt = new[] { new BtDevice("Mouse", true, 7, "mouse"), new BtDevice("Buds", true, 64, "earbuds"), new BtDevice("Old pad", false, null, "controller") };
        var v = DockedBuilder.Build(Snap([], bt: bt), new DockedOptions());
        var rows = v.Wireless.Single().Rows;
        Assert.Equal(new[] { "Bluetooth", "Buds", "Mouse" }, rows.Select(r => r.Title));
        Assert.False(rows[1].Right!.Slow); Assert.Null(rows[1].Warning);
        Assert.True(rows[2].Right!.Slow); Assert.Equal("Battery low. Charge it soon.", rows[2].Warning);
        Assert.Equal("Old pad", v.Paired.Single().Title);
        Assert.Null(v.Paired.Single().Right);
        Assert.Empty(DockedBuilder.Build(Snap([], bt: bt), new DockedOptions(Wireless: false)).Wireless);
        Assert.True(DockedBuilder.Build(Snap([], bt: [new BtDevice("A", true, 20)]), new DockedOptions()).Wireless[0].Rows[1].Right!.Slow);   // 20 % is attention
        Assert.False(DockedBuilder.Build(Snap([], bt: [new BtDevice("A", true, 21)]), new DockedOptions()).Wireless[0].Rows[1].Right!.Slow);
    }

    [Fact]
    public void ClassifiersAndFilters()
    {
        Assert.Equal("keyboard", BtKind.From(0x000540, "x")); Assert.Equal("mouse", BtKind.From(0x000580, "x"));
        Assert.Equal("headphones", BtKind.From(0x240418, "x")); Assert.Equal("earbuds", BtKind.From(null, "AirPods Pro"));
        Assert.Equal("", BtKind.From(null, "XYZ"));
        Assert.Equal("modem", Uplink.Classify(true, 243, "Cellular", true));
        Assert.Equal("iphone", Uplink.Classify(false, 6, "Apple Mobile Device Ethernet", false));
        Assert.Equal("hotspot", Uplink.Classify(false, 71, "Intel Wi-Fi", true));
        Assert.Equal("metered", Uplink.Classify(false, 6, "Realtek", true));
        Assert.Null(Uplink.Classify(false, 71, "Intel Wi-Fi", false));
        var clean = RawFilter.Clean([("Class", "USB"), ("Serial Number", "123"), ("Device Instance Id", "USB\\X\\1"), ("Bus Address", "1"), ("Container Guid", "{x}"), ("Driver", "usbhub3")]);
        Assert.Equal(new[] { "Class", "Driver" }, clean.Select(c => c.Key));
    }

    [Fact]
    public void HeadphoneJackFromEndpoints()
    {
        AudioEndpoint E(string n, string k, bool plugged, bool def = false) => new(n, k, true, def, plugged);
        var both = new[] { E("Speakers (Realtek(R) Audio)", "Speakers", true), E("Headphones (Realtek(R) Audio)", "Headphones", true, true) };
        Assert.Equal(new JackInfo(true, true, false, true), JackMap.Find(both));
        var empty = new[] { E("Speakers (Realtek(R) Audio)", "Speakers", true), E("Headphones (Realtek(R) Audio)", "Headphones", false) };
        Assert.Equal(new JackInfo(true, false, false, false), JackMap.Find(empty));
        var usbHeadset = new[] { E("Headset (Logitech USB)", "Headset", true), E("Speakers (Realtek(R) Audio)", "Speakers", true) };
        Assert.False(JackMap.Find(usbHeadset).Plugged);
        Assert.Equal("Realtek(R) Audio", JackMap.Adapter("Speakers (Realtek(R) Audio)"));
        Assert.Equal("Headphone jack · empty", DockedBuilder.Build(Snap([], audio: empty), new DockedOptions()).EmptyLine);
        Assert.Null(DockedBuilder.Build(Snap([], audio: empty), new DockedOptions(Empty: false)).EmptyLine);
        var plugged = DockedBuilder.Build(Snap([], audio: both), new DockedOptions());
        Assert.Equal("sound plays here", plugged.Ports.Single().Rows[1].Facts[0]);
    }

    [Fact]
    public void UsbPowerAndRanks()
    {
        Assert.Equal(200, UsbSpeed.PowerMa(100, 2)); Assert.Equal(896, UsbSpeed.PowerMa(112, 3)); Assert.Null(UsbSpeed.PowerMa(0, 2));
        Assert.Equal((2, 3), UsbSpeed.Ranks(2, UsbSpeed.FlagCapableSuper, 0));
        Assert.Equal("USB 2", UsbSpeed.Family(480_000_000));
    }
}
