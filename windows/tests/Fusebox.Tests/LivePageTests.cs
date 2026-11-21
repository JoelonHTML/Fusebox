using Fusebox.Core;
using Fusebox.Core.Live;
using Fusebox.Core.Look;
using Fusebox.Core.Processes;

public class LiveCardTests
{
    static LiveOptions Opt(string absolute = "ram,ssd", bool bits = false, bool ping = false) =>
        new(absolute.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(), NetBits: bits, NetPing: ping, CpuCount: 8);

    static LiveSample S() => new()
    {
        Cpu = 23.4, Gpu = 4, RamUsed = 12.4 * 1073741824, RamTotal = 16 * 1073741824.0, NetIn = 8000, NetOut = 120_000,
        DiskFree = 412e9, DiskTotal = 994e9, DiskRead = 12e6, DiskWrite = 3e6, Battery = 81, BatteryMinutes = 155, Cores = new double[8], PingMs = 18, WifiDbm = -52,
    };

    [Fact]
    public void ValuesTemplatesAndUnitsFollowTheMac()
    {
        var o = Opt();
        var cpu = LiveCards.Card(Metric.Cpu, S(), o);
        Assert.Equal(("23%", "", "100%"), (cpu.Value, cpu.Unit, cpu.Template));
        var cores = LiveCards.Card(Metric.Cpu, S(), Opt("cpu,ram"));
        Assert.Equal(("1.9", "/8", "88.8"), (cores.Value, cores.Unit, cores.Template));
        var ram = LiveCards.Card(Metric.Ram, S(), o);
        Assert.Equal(("12.4", "GB", "88.8"), (ram.Value, ram.Unit, ram.Template));
        Assert.Equal("78%", LiveCards.Card(Metric.Ram, S(), Opt("")).Value);
        var net = LiveCards.Card(Metric.Net, S(), o);
        Assert.Equal(("↓8 ↑120", "KB/s", "↓888 ↑888"), (net.Value, net.Unit, net.Template));
        Assert.Equal("", net.SideIcon);                                   // Wi-Fi
        Assert.Equal("R 12 W 3.0", LiveCards.Card(Metric.Io, S(), o).Value);   // one decimal below 10 in MB
        var ssd = LiveCards.Card(Metric.Ssd, S(), o);
        Assert.Equal(("412", "GB", "8888"), (ssd.Value, ssd.Unit, ssd.Template));
        var bat = LiveCards.Card(Metric.Battery, S(), o);
        Assert.Equal(("81%", "2:35"), (bat.Value, bat.Side));
        Assert.Equal("–", LiveCards.Card(Metric.Battery, new LiveSample(), o).Value);
    }

    [Fact]
    public void NothingBeforeTheFirstSample()
    {
        var empty = new LiveSample();
        Assert.Equal("", LiveCards.Card(Metric.Cpu, empty, Opt()).Value);       // never a fake "0%"
        Assert.Equal("", LiveCards.Card(Metric.Net, empty, Opt()).Value);
    }

    [Fact]
    public void LevelsAndSideText()
    {
        var s = S();
        s.Cpu = 85; Assert.Equal(1, LiveCards.Card(Metric.Cpu, s, Opt()).Level);
        s.Cpu = 95; Assert.Equal(2, LiveCards.Card(Metric.Cpu, s, Opt()).Level);
        s.Gpu = 90; Assert.Equal(1, LiveCards.Card(Metric.Gpu, s, Opt()).Level);
        s.Gpu = 98; Assert.Equal(2, LiveCards.Card(Metric.Gpu, s, Opt()).Level);
        s.Pressure = 2; Assert.Equal(1, LiveCards.Card(Metric.Ram, s, Opt()).Level);
        s.Pressure = 4; Assert.Equal(2, LiveCards.Card(Metric.Ram, s, Opt()).Level);
        s.DiskFree = 80e9; Assert.Equal(1, LiveCards.Card(Metric.Ssd, s, Opt()).Level);    // 92 % used
        s.DiskFree = 40e9; Assert.Equal(2, LiveCards.Card(Metric.Ssd, s, Opt()).Level);    // 96 % used
        s.Battery = 20; Assert.Equal(1, LiveCards.Card(Metric.Battery, s, Opt()).Level);
        s.Battery = 10; Assert.Equal(2, LiveCards.Card(Metric.Battery, s, Opt()).Level);
        s.Charging = true; Assert.Equal(0, LiveCards.Card(Metric.Battery, s, Opt()).Level);
        s.TempC = 96; Assert.Equal(("96°", 1), (LiveCards.Card(Metric.Cpu, s, Opt()).Side, LiveCards.Card(Metric.Cpu, s, Opt()).SideLevel));
        Assert.Equal("18 ms", LiveCards.Card(Metric.Net, S(), Opt(ping: true)).Side);
        Assert.Equal("", LiveCards.Card(Metric.Net, S(), Opt()).Side);          // netPing off by default
    }

    [Fact]
    public void BatteryGlyphByLevel()
    {
        Assert.Equal("", LiveCards.BatteryGlyph(50, true));
        Assert.Equal("", LiveCards.BatteryGlyph(88, false));
        Assert.Equal("", LiveCards.BatteryGlyph(63, false));
        Assert.Equal("", LiveCards.BatteryGlyph(38, false));
        Assert.Equal("", LiveCards.BatteryGlyph(13, false));
        Assert.Equal("", LiveCards.BatteryGlyph(12, false));
    }

    [Fact]
    public void ValueSizes()
    {
        Assert.Equal(30, LiveCards.ValueSize(Metric.Cpu, false));
        Assert.Equal(24, LiveCards.ValueSize(Metric.Net, false));
        Assert.Equal(15, LiveCards.ValueSize(Metric.Ssd, true));
        Assert.Equal(12, LiveCards.ValueSize(Metric.Io, true));
    }

    [Fact]
    public void GraphScalesAndSeries()
    {
        var o = Opt();
        Assert.Equal(16, LiveCards.GraphTop(Metric.Ram, S(), o));
        Assert.Equal(100, LiveCards.GraphTop(Metric.Cpu, S(), o));
        Assert.True(double.IsNaN(LiveCards.GraphTop(Metric.Net, S(), o)));
        var (main, second) = LiveCards.Series(Metric.Net, [S(), S()], o);
        Assert.Equal([8000d, 8000d], main);
        Assert.Equal([120_000d, 120_000d], second);
    }

    [Fact]
    public void FactsAndLowAvgHigh()
    {
        var apps = new List<AppGroup>
        {
            new() { Name = "Xcode", Kind = AppKind.App, Cpu = 61 }, new() { Name = "Windows", Kind = AppKind.Windows, Cpu = 90 },
            new() { Name = "Safari", Kind = AppKind.App, Cpu = 12 },
        };
        var peak = new Peak { Kind = PeakKind.Cpu, Start = new DateTime(2026, 11, 21, 14, 32, 0), End = new DateTime(2026, 11, 21, 14, 33, 0), Top = 94, App = "Xcode" };
        var f = LiveCards.Facts(Metric.Cpu, S(), Opt(), [12, 23, 78], apps, [peak]);
        Assert.Equal("14:32 · Xcode", f.First(x => x.Label == "Last peak").Value);
        Assert.Equal(["Xcode", "Safari"], f.Where(x => x.App != null).Select(x => x.App));   // "Windows" never named
        Assert.Equal("12 · 38 · 78%", LiveCards.LowAvgHigh(Metric.Cpu, [12, 23, 78], Opt()));
        Assert.Equal("11.9 · 12.2 · 12.8 GB", LiveCards.LowAvgHigh(Metric.Ram, [11.9, 12.1, 12.8, 12.2, 12.2], Opt()));
    }

    [Fact]
    public void WifiSignalWords()
    {
        Assert.Equal("Excellent · −52 dBm", LiveCards.WifiSignal(-52));
        Assert.Equal("Good · −60 dBm", LiveCards.WifiSignal(-60));
        Assert.StartsWith("Weak", LiveCards.WifiSignal(-80));
    }
}

public class LiveLayoutExampleTests
{
    [Fact]
    public void DefaultNarrowWindowIsCalmAndComplete()
    {
        // 300 × 340, 6 metrics: one column × 6 rows in every row style, never scrolling (rows share the height).
        foreach (var pad in new double[] { 15, 10, 22 })
        {
            var l = LiveLayout.Compute(300, 340, 6, pad, false);
            Assert.Equal((1, 6), (l.Cols, l.Rows));
            Assert.True(l.Tier <= Tier.S);
        }
        Assert.Equal(Tier.S, LiveLayout.Compute(300, 340, 6, 15, false).Tier);    // lines: 40 pt per row ≥ 38
        Assert.Equal(Tier.XS, LiveLayout.Compute(300, 340, 6, 22, false).Tier);   // cards/blocks/glass: 33 pt
        Assert.True(LiveLayout.MinHeight(6, 15) <= 340);
        Assert.Equal(Tier.M, LiveLayout.Compute(300, 700, 6, 15, false).Tier);
    }
}

public class SparkTests
{
    [Fact]
    public void PointsAreRightAlignedAndScaled()
    {
        var p = SparkGeometry.Points([0, 50, 100], 100, 40, 100);
        Assert.Equal((0d, 40d), p[0]);
        Assert.Equal((50d, 20d), p[1]);
        Assert.Equal((100d, 0d), p[2]);
        Assert.Equal(40, SparkGeometry.Y(double.NaN, 40, 100));
        Assert.Equal(0, SparkGeometry.Y(500, 40, 100));         // clamped
        Assert.Equal(1, SparkGeometry.AutoTop([0, 0.5], null));
        Assert.Equal(9, SparkGeometry.AutoTop([1, 2], [9, 3]));
    }

    [Fact]
    public void RoundedPolylineCutsCornersAtTwentyPercent()
    {
        var segs = SparkGeometry.Rounded([(0, 0), (10, 10), (20, 0)]);
        Assert.Equal(3, segs.Count);
        Assert.Equal(new Seg(false, 0, 0, 8, 8), segs[0]);          // 20 % toward the previous point
        Assert.Equal(new Seg(true, 10, 10, 12, 8), segs[1]);        // vertex as control, 20 % toward the next
        Assert.Equal(new Seg(false, 0, 0, 20, 0), segs[2]);
        Assert.Empty(SparkGeometry.Rounded([(0, 0)]));
    }

    [Fact]
    public void ThinningAveragesBuckets()
    {
        var v = Enumerable.Range(0, 1000).Select(i => (double)i).ToList();
        Assert.False(SparkGeometry.NeedsThinning(150, 300));
        Assert.True(SparkGeometry.NeedsThinning(1000, 300));
        var t = SparkGeometry.Thin(v, 100);
        Assert.Equal(100, t.Count);
        Assert.Equal(4.5, t[0]);
        Assert.Equal(150, SparkGeometry.Thin(v.Take(150).ToList(), 300).Count);
    }

    [Fact]
    public void GlideKinds()
    {
        Assert.Equal(GlideKind.Slide, SparkGeometry.Detect([1, 2, 3, 4], [2, 3, 4, 5]));
        Assert.Equal(GlideKind.Grow, SparkGeometry.Detect([1, 2, 3], [1, 2, 3, 4]));
        Assert.Equal(GlideKind.None, SparkGeometry.Detect([1, 2, 3, 4], [9, 9, 9, 9]));
        Assert.Equal(GlideKind.None, SparkGeometry.Detect([1], [1, 2]));
        Assert.False(SparkGeometry.ShouldRedraw(0.2, 1));
        Assert.True(SparkGeometry.ShouldRedraw(0.3, 2));           // half a physical pixel at 200 %
        Assert.Equal(2, SparkGeometry.Nearest(100, 100, 3));
        Assert.Equal(0, SparkGeometry.Nearest(-5, 100, 3));
    }

    [Fact]
    public void MotionTiming()
    {
        Assert.Equal(0, Motion.FadeIn(0));
        Assert.Equal(1, Motion.FadeIn(6));
        Assert.Equal(0.5, Motion.FadeIn(3), 6);
        Assert.True(Motion.IntroActive(1.1));
        Assert.False(Motion.IntroActive(1.3));
        Assert.Equal(0.4, Motion.Pace(false));
        Assert.Equal(0.03 + 0.12, Motion.RowDelay(3, 1), 9);
        Assert.Equal(0.8 * 0.4, Motion.RevealDuration(0.4), 9);
        Assert.Equal(0, Motion.RevealEase(0));
        Assert.Equal(1, Motion.RevealEase(1));
        Assert.True(Motion.RevealEase(0.25) > 0.25);               // fast start
        double prev = 0;
        for (double x = 0.05; x <= 1; x += 0.05) { var y = Motion.RevealEase(x); Assert.True(y >= prev); prev = y; }
    }
}

public class PeakInfoTests
{
    static Peak P(PeakKind k, int seconds, double top, string? app = "Xcode") =>
        new() { Kind = k, Start = new DateTime(2026, 11, 21, 14, 32, 0), End = new DateTime(2026, 11, 21, 14, 32, 0).AddSeconds(seconds), Top = top, At = new DateTime(2026, 11, 21, 14, 32, 0), App = app, AppValue = 61 };

    [Fact]
    public void TextsLikeTheMac()
    {
        Assert.Equal("2 s", PeakInfo.Length(1));
        Assert.Equal("8 s", PeakInfo.Length(8));
        Assert.Equal("5 min", PeakInfo.Length(300));
        Assert.Equal("CPU 94%", PeakInfo.RowValue(P(PeakKind.Cpu, 8, 94), true));
        Assert.Equal("97°", PeakInfo.Value(P(PeakKind.Temp, 8, 97)));
        Assert.Equal("Xcode 61%", PeakInfo.CauseText(P(PeakKind.Cpu, 8, 94)));
        Assert.Null(PeakInfo.CauseText(P(PeakKind.Cpu, 8, 94, null)));
        Assert.Equal("Peak 14:32 · 94% · Xcode", PeakInfo.Label(P(PeakKind.Cpu, 8, 94)));
    }

    [Fact]
    public void DetailRangeFromPeakAge()
    {
        var now = new DateTime(2026, 11, 21, 15, 0, 0);
        Assert.Equal(TimeRange.Short, PeakInfo.RangeFor(now.AddSeconds(-100), now, 5));
        Assert.Equal(TimeRange.Hour, PeakInfo.RangeFor(now.AddSeconds(-1000), now, 5));
        Assert.Equal(TimeRange.Today, PeakInfo.RangeFor(now.AddHours(-3), now, 5));
    }

    [Fact]
    public void CauseIsTheBiggestAppButNeverWindows()
    {
        var apps = new List<AppGroup>
        {
            new() { Name = "Windows", Kind = AppKind.Windows, Cpu = 90 }, new() { Name = "Xcode", Kind = AppKind.App, Cpu = 61, Ram = 3L << 30 },
            new() { Name = "Chrome", Kind = AppKind.App, Cpu = 10, Ram = 4L << 30 },
        };
        Assert.Equal("Xcode", PeakInfo.Cause(PeakKind.Cpu, apps)!.Value.Name);
        Assert.Equal("Chrome", PeakInfo.Cause(PeakKind.Mem, apps)!.Value.Name);
        Assert.Null(PeakInfo.Cause(PeakKind.Io, apps));
        Assert.Null(PeakInfo.Cause(PeakKind.Gpu, apps));
    }

    [Fact]
    public void SustainedPeakAlertRules()
    {
        var now = new DateTime(2026, 11, 21, 15, 0, 0);
        var last = new Dictionary<string, DateTime>();
        var running = new[] { (P(PeakKind.Cpu, 110, 92), (string?)"Xcode") };   // 2 min − 10 s slack = 110 s
        Assert.Single(PeakAlerts.Due(running, 2, 30, last, _ => true, now));
        Assert.Empty(PeakAlerts.Due(running, 2, 30, last, _ => true, now.AddMinutes(10)));          // cooldown 30 min
        Assert.Single(PeakAlerts.Due(running, 2, 30, last, _ => true, now.AddMinutes(31)));
        Assert.Empty(PeakAlerts.Due([(P(PeakKind.Cpu, 100, 92), "Safari")], 2, 30, new Dictionary<string, DateTime>(), _ => true, now));   // too short
        Assert.Empty(PeakAlerts.Due(running, 2, 30, new Dictionary<string, DateTime>(), _ => false, now));                                // app gone
        Assert.Empty(PeakAlerts.Due([(P(PeakKind.Cpu, 300, 92, null), (string?)null)], 2, 30, new Dictionary<string, DateTime>(), _ => true, now));
        var n = PeakAlerts.Due(running, 2, 30, new Dictionary<string, DateTime>(), _ => true, now)[0];
        Assert.Equal("Xcode is keeping your CPU busy", n.Title);
        Assert.Equal("CPU 92% for 1 minute", n.Body);
        Assert.Equal("CPU 92% for 3 minutes", PeakAlerts.Body(P(PeakKind.Cpu, 200, 92)));
    }
}

public class TrayGlyphTests
{
    static TrayItem I(string abbr, string text, double f, params double[] h) => new(abbr, text, f, h);
    static int Lit(uint[] px) => px.Count(p => (p >> 24) > 0);

    [Fact]
    public void TextAndTooltip()
    {
        var items = new[] { I("CPU", "23%", 0.23), I("RAM", "12.0 GB", 0.7), I("NET", "↓1.2 MB/s", 0.1) };
        Assert.Equal("CPU 23%  RAM 12.0 GB  NET ↓1.2 MB/s", TrayInfo.Tooltip(items));
        Assert.Equal(127, TrayInfo.Tooltip([I("CPU", new string('9', 300), 0)]).Length);
        Assert.Equal("12", TrayGlyph.Number("12.4 GB"));
        Assert.Equal("1", TrayGlyph.Number("↓1.2 MB/s"));
        Assert.Equal("", TrayGlyph.Number("–"));
        Assert.Equal("100", TrayGlyph.Number("100%"));
    }

    [Fact]
    public void EveryStyleDrawsSomethingAndOnlyChangesWithTheValue()
    {
        var a = new[] { I("CPU", "23%", 0.23, 0.1, 0.2, 0.3), I("RAM", "50%", 0.5, 0.5, 0.5) };
        foreach (var style in new[] { "text", "compact", "bars", "graph", "icon" })
        {
            var one = TrayGlyph.Render(style, 16, false, a);
            Assert.Equal(256, one.Length);
            Assert.True(Lit(one) > 0, style);
            Assert.Equal(one, TrayGlyph.Render(style, 16, false, a));                 // same input = same pixels (no HICON rebuild)
        }
        var b = new[] { I("CPU", "24%", 0.24, 0.1, 0.2, 0.3), I("RAM", "50%", 0.5, 0.5, 0.5) };
        Assert.NotEqual(TrayGlyph.Render("text", 16, false, a), TrayGlyph.Render("text", 16, false, b));
        Assert.Equal(TrayGlyph.Render("icon", 16, false, a), TrayGlyph.Render("icon", 16, false, b));
        Assert.NotEqual(TrayGlyph.Render("icon", 16, false, a), TrayGlyph.Render("icon", 16, false, a, alert: true));
    }

    [Fact]
    public void InkFollowsTheTaskbarTheme()
    {
        var px = TrayGlyph.Render("bars", 16, false, [I("CPU", "50%", 0.5)]);
        Assert.All(px.Where(p => (p >> 24) == 255), p => Assert.Equal(0xFFFFFFu, p & 0xFFFFFF));
        var light = TrayGlyph.Render("bars", 16, true, [I("CPU", "50%", 0.5)]);
        Assert.All(light.Where(p => (p >> 24) == 255), p => Assert.Equal(0x202020u, p & 0xFFFFFF));
    }

    [Fact]
    public void BarsFillFromTheBottom()
    {
        var px = TrayGlyph.Render("bars", 16, false, [I("CPU", "100%", 1), I("RAM", "0%", 0)]);
        int full = px.Count(p => (p >> 24) == 255);
        Assert.Equal(14 * 6 + 6, full);           // first bar solid (6 × 14), second bar only its 1-pixel minimum
    }

    [Fact]
    public void MenuValueAndPick()
    {
        var s = new LiveSample { Cpu = 23, RamUsed = 12 * 1073741824.0, RamTotal = 16 * 1073741824.0, NetIn = 1.2e6, NetOut = 0, Cores = new double[8] };
        var o = new LiveOptions(new HashSet<string> { "ram", "cpu" }, CpuCount: 8);
        Assert.Equal("1.8 cores", TrayInfo.MenuValue(Metric.Cpu, s, o));
        Assert.Equal("12.0 GB", TrayInfo.MenuValue(Metric.Ram, s, o));
        Assert.Equal("↓1.2 MB/s", TrayInfo.MenuValue(Metric.Net, s, o));
        Assert.Equal("23%", TrayInfo.MenuValue(Metric.Cpu, s, o with { Absolute = new HashSet<string>() }));
        Assert.Equal([Metric.Cpu, Metric.Ram], TrayInfo.Pick("cpu,gpu,ram,net,ssd", m => m is Metric.Cpu or Metric.Ram or Metric.Net, 2));
        Assert.Equal(4, TrayInfo.Pick("cpu,gpu,ram,net,ssd", _ => true).Count);
        Assert.Equal(0.5, TrayInfo.Fraction(Metric.Net, new LiveSample { NetIn = 30e6, NetOut = 20e6 }));
    }
}
