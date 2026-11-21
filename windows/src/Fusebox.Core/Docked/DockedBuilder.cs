using static Fusebox.Core.Strings;

namespace Fusebox.Core.Docked;

/// <summary>
/// Snapshot to view (spec §7): ports grouped by USB root port, source row ("Hub" or the device), devices behind it, displays,
/// headphone jack, wireless, other devices. Where Windows can't tell (PD contracts, cable e-marker, charger PDOs) nothing is invented.
/// </summary>
public static class DockedBuilder
{
    const string GlyphUsb = "", GlyphHub = "", GlyphInfo = "", GlyphDisplay = "", GlyphJack = "", GlyphBt = "",
        GlyphDevice = "", GlyphBolt = "", GlyphLink = "", GlyphWarn = "";

    // ----- mutable tree used while building -----
    sealed class N
    {
        public string Id = "", Glyph = "", Title = ""; public bool Dim, Port; public string? Info, Warning;
        public List<string> Facts = new(); public RightValue? Right; public Detail? Detail; public List<N> Kids = new();
    }

    sealed class U { public UsbNode D = null!; public List<U> Kids = new(); }

    static List<U> UsbTree(IReadOnlyList<UsbNode> usb)
    {
        var roots = new List<U>(); var stack = new List<U>();
        foreach (var d in usb)
        {
            var u = new U { D = d };
            while (stack.Count > d.Depth) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0) roots.Add(u); else stack[^1].Kids.Add(u);
            stack.Add(u);
        }
        return roots;
    }

    static bool IsBillboard(UsbNode d) => d.Class == 0x11 || d.Name.Contains("Billboard", StringComparison.OrdinalIgnoreCase);

    /// <summary>Device leaves below a node (hubs flattened away, Billboard left out).</summary>
    static IEnumerable<U> Leaves(U u)
    {
        if (!u.D.IsHub) { if (!IsBillboard(u.D)) yield return u; yield break; }
        foreach (var k in u.Kids) foreach (var l in Leaves(k)) yield return l;
    }

    public static DockedView Build(DockedSnapshot s, DockedOptions o)
    {
        var roots = UsbTree(s.Usb);
        // Roots the OS says a user can plug into are the ports; with no such flag anywhere, every root counts as a port.
        bool anyFlag = roots.Any(r => r.D.Connectable == true);
        var portRoots = roots.Where(r => !anyFlag || r.D.Connectable == true).OrderBy(r => r.D.Root).ThenBy(r => r.D.Path, StringComparer.Ordinal).ToList();
        var otherRoots = roots.Where(r => !portRoots.Contains(r)).ToList();

        // DisplayLink adapter: the first USB device from vendor 17E9 in a port.
        U? link = portRoots.SelectMany(All).FirstOrDefault(u => u.D.Vid == 0x17E9 && !u.D.IsHub);
        var external = o.Displays ? s.Displays.Where(d => !DisplayMap.IsBuiltIn(d.Tech)).ToList() : [];
        var viaLink = link == null ? [] : external.Where(d => DisplayMap.IsIndirect(d.Tech)).ToList();
        var loose = external.Except(viaLink).ToList();

        // ----- port groups -----
        var groups = new List<(string Id, N Root)>();
        int number = 0;
        foreach (var r in portRoots)
        {
            number++;
            groups.Add(("port" + r.D.Path, PortNode(r, number, o, link, viaLink)));
        }
        if (loose.Count > 0)
        {
            var g = new N { Id = "displays", Glyph = GlyphDisplay, Title = L("Displays"), Port = true, Facts = { L("not linked to a port") } };
            foreach (var d in loose) g.Kids.Add(DisplayNode(d, o));
            groups.Add(("displays", g));
        }
        var jack = JackMap.Find(s.Audio);
        if (jack.Plugged) groups.Add(("jack", JackNode(jack)));

        var ports = groups.Select(g => new Group(g.Id, Flatten(g.Root))).ToList();

        // ----- wireless -----
        var wireless = new List<Group>();
        var paired = new List<TreeRow>();
        int btConnected = 0;
        if (o.Wireless)
        {
            if (s.Uplink is { } up) wireless.Add(new("uplink", Flatten(UplinkNode(up))));
            if (s.Bluetooth is { } bt)
            {
                var on = bt.Where(b => b.Connected).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
                btConnected = on.Count;
                if (on.Count > 0)
                {
                    var g = new N { Id = "bt", Glyph = GlyphBt, Title = "Bluetooth", Port = true, Facts = { L("%lld connected", on.Count) } };
                    foreach (var b in on) g.Kids.Add(BtNode(b, false));
                    wireless.Add(new("bt", Flatten(g)));
                }
                foreach (var b in bt.Where(b => !b.Connected).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)) paired.AddRange(Flatten(BtNode(b, true)));
            }
        }

        // ----- other devices (not behind a user port) -----
        Group? other = null;
        var otherNodes = new List<N>();
        foreach (var r in otherRoots)
            if (r.D.IsHub) { if (o.Internals) otherNodes.Add(HubTree(r, o, null, null)); else otherNodes.AddRange(Leaves(r).Select(l => DeviceNode(l, o, null, null))); }
            else if (!IsBillboard(r.D)) otherNodes.Add(DeviceNode(r, o, null, null));
        if (otherNodes.Count > 0) other = new Group("other", otherNodes.SelectMany(Flatten).ToList());

        // ----- header -----
        int devices = portRoots.SelectMany(Leaves).Count();
        long fastest = portRoots.Select(r => r.D.Bps).DefaultIfEmpty(0).Max();
        var chips = new List<Chip>();
        if (devices > 0) chips.Add(new("", devices.ToString(), devices == 1 ? L("device") : L("devices"), null));
        if (btConnected > 0) chips.Add(new(GlyphBt, btConnected.ToString(), "Bluetooth", null));
        if (external.Count > 0)
            chips.Add(new(GlyphDisplay, external.Count.ToString(), external.Count == 1 ? L("display") : L("displays"),
                string.Join("\n", s.Displays.Select(d => $"{(DisplayMap.IsBuiltIn(d.Tech) ? L("Built-in") : d.Name)}: {d.Width} × {d.Height} · {DockedText.Trim(d.Hz)} Hz"))));
        if (fastest > 0) chips.Add(new(GlyphLink, DockedText.Speed(fastest, o.Bytes), L("link"), null));
        var header = Head(s.Power, chips, portRoots.Any(r => r.D.TypeC == true));

        string? emptyLine = o.Empty && jack.Present && !jack.Plugged ? L("Headphone jack") + L(" · empty") : null;
        string? emptyState = ports.Count == 0 && wireless.Count == 0 && paired.Count == 0 && other == null && emptyLine == null ? L("No USB devices found.") : null;

        var view = new DockedView(header, ports, emptyLine, wireless, paired, other, emptyState, "");
        return view with { Signature = ViewSig.Of(header, view.AllRows, emptyState, emptyLine) };
    }

    static IEnumerable<U> All(U u) { yield return u; foreach (var k in u.Kids) foreach (var x in All(k)) yield return x; }

    // ----- header -----
    static Header Head(PowerInfo p, List<Chip> chips, bool hasUsbC)
    {
        string big, caption = ""; string? unit = null;
        if (p.Watts is > 0.05 && p.OnAc == true) { big = DockedText.Trim(p.Watts.Value); unit = "W"; caption = L("into this PC"); }
        else big = p.OnAc == true ? L("Plugged in") : p.OnAc == false ? L("On battery") : L("Power status unavailable.");
        bool bat = p.HasBattery && p.Percent != null;
        return new Header(big, unit, caption.Length > 0 ? caption : null, bat ? p.Percent!.Value.ToString() : null, bat ? DockedText.BatteryText(p) : null, chips,
            hasUsbC && p.OnAc == true ? L("Windows doesn't share charger and cable details with apps.") : null);
    }

    // ----- flatten (connector columns, spec §7.7) -----
    static List<TreeRow> Flatten(N root)
    {
        var res = new List<TreeRow>();
        void Walk(N n, bool last, bool isRoot, List<Conn> ancestors)
        {
            var mine = isRoot ? [] : new List<Conn>(ancestors) { last ? Conn.Elbow : Conn.Tee };
            res.Add(new TreeRow(n.Id, mine, n.Glyph, n.Title, n.Dim, n.Port, n.Info, n.Facts.Take(4).ToList(), n.Right, n.Warning, n.Detail));
            var next = isRoot ? [] : new List<Conn>(ancestors) { last ? Conn.None : Conn.Pass };
            for (int i = 0; i < n.Kids.Count; i++) Walk(n.Kids[i], i == n.Kids.Count - 1, false, next);
        }
        Walk(root, true, true, []);
        return res;
    }

    // ----- ports -----
    const string SlowDevice = "Runs at USB 2 speed, but it can do USB 3. Try another cable or port.";

    static string SlowText(UsbNode d) => d.Bps <= 480_000_000 && d.MaxBps >= 5_000_000_000 ? L(SlowDevice) : L("Runs slower than the port, cable and device allow. Try another cable.");

    static N PortNode(U r, int number, DockedOptions o, U? link, List<DisplayInfo>? viaLink)
    {
        var d = r.D;
        var port = new N { Id = "p:" + d.Path, Glyph = GlyphUsb, Title = L("%@ port %lld", d.TypeC == true ? "USB-C" : "USB", number), Port = true };
        if (d.Bps > 0) port.Facts.Add(UsbSpeed.Family(d.Bps));
        int count = Leaves(r).Count();
        if (d.IsHub && count > 0) port.Facts.Add($"{count} " + (count == 1 ? L("device") : L("devices")));
        if (d.Bps > 0) port.Right = DockedText.Link(d.Bps, d.MaxBps, d.Slow, o.Bytes);
        if (d.Slow) port.Warning = SlowText(d);
        port.Detail = PortDetail(port.Title, r, o);

        if (!d.IsHub) { port.Kids.Add(DeviceNode(r, o, link, viaLink, source: true)); return port; }
        var hubName = d.Name.Contains("hub", StringComparison.OrdinalIgnoreCase) && d.Name.Length < 28 ? L("Hub") : d.Name;
        var src = new N { Id = "d:" + d.Path, Glyph = GlyphHub, Title = hubName };
        if (d.Maker is { Length: > 0 }) src.Facts.Add(d.Maker);
        src.Detail = DeviceDetail(d, o, GlyphHub, hubName);
        if (o.Internals)
        {
            foreach (var k in HubKids(r, o, link, viaLink)) src.Kids.Add(k);
        }
        else foreach (var l in Leaves(r)) src.Kids.Add(DeviceNode(l, o, link, viaLink));
        port.Kids.Add(src);
        return port;
    }

    /// <summary>Internals on: the real tree. Empty hubs dropped, a hub whose only child is another hub becomes one row ("{n} hubs").</summary>
    static IEnumerable<N> HubKids(U hub, DockedOptions o, U? link, List<DisplayInfo>? viaLink)
    {
        foreach (var k in hub.Kids)
        {
            if (k.D.IsHub) { if (!Leaves(k).Any() && !k.Kids.Any(x => IsBillboard(x.D))) continue; yield return HubTree(k, o, link, viaLink); }
            else yield return DeviceNode(k, o, link, viaLink);
        }
    }

    static N HubTree(U hub, DockedOptions o, U? link, List<DisplayInfo>? viaLink)
    {
        int chain = 1; var cur = hub;
        while (cur.Kids.Count == 1 && cur.Kids[0].D.IsHub) { cur = cur.Kids[0]; chain++; }
        var n = new N { Id = "d:" + hub.D.Path, Glyph = GlyphHub, Title = hub.D.Name, Dim = true, Detail = DeviceDetail(hub.D, o, GlyphHub, hub.D.Name) };
        if (hub.D.Maker is { Length: > 0 }) n.Facts.Add(hub.D.Maker);
        if (chain > 1) n.Facts.Add(L("%lld hubs", chain));
        if (hub.D.Bps > 0) n.Right = DockedText.Link(hub.D.Bps, 0, false, o.Bytes);
        n.Kids.AddRange(HubKids(cur, o, link, viaLink));
        return n;
    }

    static N DeviceNode(U u, DockedOptions o, U? link, List<DisplayInfo>? viaLink, bool source = false)
    {
        var d = u.D;
        bool bb = IsBillboard(d);
        var n = new N { Id = "d:" + d.Path, Glyph = DeviceGlyph(d), Title = d.Name, Dim = bb || d.IsHub };
        if (d.Maker is { Length: > 0 }) n.Facts.Add(d.Maker);
        if (d.Bps > 0) n.Facts.Add(UsbSpeed.Family(d.Bps));
        if (d.Ma is int ma) n.Facts.Add(L("%lld mA power budget", ma));
        if (bb) { n.Facts.Clear(); n.Facts.Add(L("mode info from the dock")); n.Info = L("Billboard: the dock tells the PC which extra modes (such as video over USB-C) are not active."); }
        if (!source && d.Bps > 0 && !bb) { n.Right = DockedText.Link(d.Bps, d.MaxBps, d.Slow, o.Bytes); if (d.Slow) n.Warning = SlowText(d); }
        n.Detail = DeviceDetail(d, o, n.Glyph, d.Name);
        if (link != null && viaLink is { Count: > 0 } && ReferenceEquals(link, u))
        {
            n.Facts.Insert(0, viaLink.Count == 1 ? L("1 display via DisplayLink") : L("%lld displays via DisplayLink", viaLink.Count));
            n.Info = L("DisplayLink sends the picture compressed over USB, and the PC's processor does the work. Fine for office work; not for games or video (motion can stutter, some protected video won't play).");
            foreach (var disp in viaLink) n.Kids.Add(DisplayNode(disp, o));
        }
        return n;
    }

    static string DeviceGlyph(UsbNode d)
    {
        var n = d.Name.ToLowerInvariant();
        string[][] map =
        [
            ["keyboard", ""], ["mouse", ""], ["ethernet", ""], ["lan", ""], ["audio", ""], ["headset", ""],
            ["card", ""], ["reader", ""], ["ssd", ""], ["disk", ""], ["drive", ""], ["storage", ""],
            ["camera", ""], ["webcam", ""], ["display", ""], ["hdmi", ""], ["phone", ""],
        ];
        foreach (var m in map) if (n.Contains(m[0])) return m[1];
        return d.IsHub ? GlyphHub : IsBillboard(d) ? GlyphInfo : GlyphDevice;
    }

    // ----- details -----
    static Detail PortDetail(string title, U r, DockedOptions o)
    {
        var d = r.D;
        var facts = new List<Fact> { new(L("Type"), d.TypeC == true ? "USB-C" : "USB") };
        if (d.Bps > 0) facts.Add(new(L("Speed"), DockedText.Speed(d.Bps, o.Bytes)));
        if (d.MaxBps > d.Bps) facts.Add(new(L("Can do"), DockedText.Speed(d.MaxBps, o.Bytes)));
        if (d.Speed != null) facts.Add(new(L("Link"), d.Speed));
        var notes = new List<Note>();
        if (d.Slow) notes.Add(new(NoteLevel.Warn, SlowText(d)));
        else if (d.Bps >= 5_000_000_000) notes.Add(new(NoteLevel.Good, L("Fast data (SuperSpeed) link is up.")));
        var groups = new List<FactGroup> { new(L("Connection"), GlyphInfo, facts) };
        return new Detail(title, GlyphUsb, d.Bps > 0 ? DockedText.Link(d.Bps, d.MaxBps, d.Slow, o.Bytes) : null, notes, groups, []);
    }

    static Detail DeviceDetail(UsbNode d, DockedOptions o, string glyph, string title)
    {
        var facts = new List<Fact>();
        if (d.Bps > 0) facts.Add(new(L("Speed"), DockedText.Speed(d.Bps, o.Bytes)));
        if (d.Speed != null) facts.Add(new(L("Protocol"), d.Speed));
        if (d.Slow && d.Max != null) facts.Add(new(L("Can do"), d.Max));
        if (d.TypeC is bool tc && d.Depth == 0) facts.Add(new("USB-C", tc ? L("Yes") : L("No")));
        if (d.Ma is int ma) facts.Add(new(L("Power"), L("%lld mA power budget", ma)));
        if (d.Maker is { Length: > 0 }) facts.Add(new(L("Maker"), d.Maker));
        if (d.Vid is int v) facts.Add(new(L("IDs"), d.Pid is int p ? L("vendor %@ · product %@", v.ToString("X4"), p.ToString("X4")) : L("vendor %@", v.ToString("X4"))));
        var notes = new List<Note>();
        if (d.Slow) notes.Add(new(NoteLevel.Warn, SlowText(d)));
        if (IsBillboard(d)) notes.Add(new(NoteLevel.Info, L("Billboard device present: the dock or adapter reports that an extra mode (such as video over USB-C) is not active or not supported on this connection.")));
        return new Detail(title, glyph, d.Bps > 0 ? DockedText.Link(d.Bps, d.MaxBps, d.Slow, o.Bytes) : null, notes,
            facts.Count > 0 ? [new FactGroup("USB", GlyphUsb, facts)] : [], RawFilter.Clean(d.Raw));
    }

    // ----- displays -----
    static N DisplayNode(DisplayInfo d, DockedOptions o)
    {
        var n = new N { Id = "disp:" + d.Name + ":" + d.Width + "x" + d.Height, Glyph = GlyphDisplay, Title = d.Name };
        n.Facts.Add(d.Connection);
        if (d.Width > 0) n.Facts.Add($"{d.Width} × {d.Height}");
        if (d.Scale is int sc && sc != 100 && sc > 0 && d.Width > 0) n.Facts.Add(L("looks like %@", $"{d.Width * 100 / sc} × {d.Height * 100 / sc}"));
        if (d.Hdr == true) n.Facts.Add(L("HDR"));
        if (d.Hz > 0) n.Right = DockedText.Hertz(d.Hz, d.MaxHz);
        n.Warning = DisplayWarning(d);
        if (DisplayMap.IsIndirect(d.Tech)) n.Info = L("DisplayLink sends the picture compressed over USB, and the PC's processor does the work. Fine for office work; not for games or video (motion can stutter, some protected video won't play).");
        n.Detail = DisplayDetail(d, n);
        return n;
    }

    public static string? DisplayWarning(DisplayInfo d)
    {
        if (d.MaxHz > d.Hz + 1 && d.Hz > 0)
            return L("Runs at %@ but can do %@. The cable, adapter or port may limit %@.", DisplayMap.HzText(d.Hz), DisplayMap.HzText(d.MaxHz), DockedText.ModeName(d.NativeW > 0 ? d.NativeW : d.Width, d.NativeH > 0 ? d.NativeH : d.Height, d.MaxHz));
        if (d.Width > 0 && d.NativeW > 0 && (long)d.Width * d.Height < (long)d.NativeW * d.NativeH)
            return L("Runs at %@ pixels but the display has %@, so text looks soft. Check Settings › System › Display, the cable or the port.", $"{d.Width} × {d.Height}", $"{d.NativeW} × {d.NativeH}");
        return null;
    }

    static Detail DisplayDetail(DisplayInfo d, N row)
    {
        var pic = new List<Fact>();
        if (d.Width > 0)
        {
            bool native = d.NativeW == 0 || (d.Width == d.NativeW && d.Height == d.NativeH);
            pic.Add(new(L("Resolution"), $"{d.Width} × {d.Height}" + (d.NativeW == 0 ? "" : native ? L(" (native)") : L(" (native %@)", $"{d.NativeW} × {d.NativeH}"))));
            if (d.Scale is int sc && sc != 100 && sc > 0) pic.Add(new(L("Looks like"), $"{d.Width * 100 / sc} × {d.Height * 100 / sc}"));
        }
        if (d.Hz > 0) pic.Add(new(L("Refresh"), DisplayMap.HzText(d.Hz) + (d.MaxHz > d.Hz + 1 ? " (" + L("can do %@", DisplayMap.HzText(d.MaxHz)) + ")" : "")));
        if (d.Scale is int sc2) pic.Add(new(L("Scale"), sc2 + " %"));
        pic.Add(new("HDR", d.Hdr == null ? L("No") : d.Hdr == true ? L("On") : L("Off (supported)")));
        if (d.Bits is int bits and > 0) pic.Add(new(L("Color"), L("%lld bits per channel", bits)));
        var conn = new List<Fact> { new(L("Type"), DisplayType(d)) };
        var notes = new List<Note>();
        if (row.Warning != null) notes.Add(new(NoteLevel.Warn, row.Warning));
        if (DisplayMap.IsIndirect(d.Tech) && row.Info != null) notes.Add(new(NoteLevel.Info, row.Info));
        return new Detail(d.Name, GlyphDisplay, d.Hz > 0 ? new(null, DockedText.Trim(d.Hz), "Hz", d.MaxHz > d.Hz + 1) : null, notes,
            [new FactGroup(L("Picture"), GlyphDisplay, pic), new FactGroup(L("Connection"), GlyphLink, conn)], []);
    }

    static string DisplayType(DisplayInfo d) => d.Tech switch
    {
        5 => "HDMI",
        10 or 12 => "DisplayPort",
        18 => L("USB-C (DP alt mode)"),
        16 or 17 => L("DisplayLink (compressed video over USB)"),
        _ => d.Connection,
    };

    // ----- headphone jack -----
    static N JackNode(JackInfo j)
    {
        var port = new N { Id = "jack", Glyph = GlyphJack, Title = L("Headphone jack"), Port = true, Facts = { "3.5 mm" } };
        var dev = new N { Id = "jack:dev", Glyph = GlyphJack, Title = j.Headset ? L("Headset") : L("Headphones or speakers"), Facts = { j.InUse ? L("sound plays here") : L("not used for sound") } };
        if (!j.InUse) dev.Info = L("Plugged in, but your PC sends sound elsewhere. Choose it in Settings › System › Sound.");
        dev.Detail = new Detail(dev.Title, GlyphJack, null, [],
            [new FactGroup(dev.Title, GlyphJack, [new(j.Headset ? L("Headset with microphone") : L("Headphones or speakers"), L("connected")), new(L("Sound output"), j.InUse ? L("this jack") : L("elsewhere"))])], []);
        port.Kids.Add(dev);
        return port;
    }

    // ----- wireless -----
    static N UplinkNode(UplinkInfo u)
    {
        var n = new N { Id = "uplink", Glyph = Uplink.Glyph(u.Kind), Title = Uplink.Title(u.Kind), Port = true };
        n.Facts.Add(Uplink.Via(u.Kind));
        if (u.Metered && u.Kind != "metered") n.Facts.Add(L("metered"));
        var f = new List<Fact>();
        if (u.Interface is { Length: > 0 }) f.Add(new(L("Interface"), u.Interface));
        f.Add(new(L("Through"), Uplink.Via(u.Kind)));
        f.Add(new(L("Metered"), u.Metered ? L("Yes: Windows and apps may hold back big downloads and updates") : L("No")));
        n.Detail = new Detail(n.Title, n.Glyph, null, [], [new FactGroup(n.Title, n.Glyph, f)], []);
        return n;
    }

    static N BtNode(BtDevice b, bool pairedOnly)
    {
        var n = new N { Id = "bt:" + b.Name, Glyph = BtKind.Glyph(b.Kind), Title = b.Name, Dim = pairedOnly };
        n.Facts.Add(BtKind.Title(b.Kind));
        var f = new List<Fact> { new(L("Type"), BtKind.Title(b.Kind)) };
        var notes = new List<Note>();
        if (!pairedOnly && b.Battery is int pct)
        {
            n.Right = new RightValue(null, pct.ToString(), "%", pct <= 20, DockedText.BatteryGlyph(pct));
            f.Add(new(L("Battery"), pct + "%"));
            if (pct <= 10) { n.Warning = L("Battery low. Charge it soon."); notes.Add(new(NoteLevel.Warn, n.Warning)); }
        }
        n.Detail = new Detail(b.Name, n.Glyph, n.Right, notes, [new FactGroup("Bluetooth", GlyphBt, f)], []);
        return n;
    }
}
