using Fusebox.Core;
using Fusebox.Core.Alerts;
using Fusebox.Core.Settings;

public class AlertTests
{
    static readonly DateTime T0 = new(2026, 1, 5, 10, 0, 0);

    static List<LiveSample> Minute(double cpu, double step = 2, Action<LiveSample>? more = null) =>
        Enumerable.Range(0, (int)(60 / step)).Select(i => { var s = new LiveSample { Time = T0.AddSeconds(i * step), Cpu = cpu }; more?.Invoke(s); return s; }).ToList();

    static AlertInput In(List<LiveSample> l, DateTime now, bool notify = true, bool ping = true, int cooldown = 30, AppUse? top = null) =>
        new(l, 2, notify, ping, cooldown, now, top, top);

    static AlertSetting[] Only(string id, double limit, int min) => [new(id, true, limit, min)];

    [Fact]
    public void CpuNeedsAverageAtLimit()
    {
        Assert.NotNull(AlertKinds.Check("cpu", Minute(95), null, null, 90, 5, true));
        Assert.Null(AlertKinds.Check("cpu", Minute(95), null, null, 99, 5, true));
        Assert.Equal("Your PC has been at 95% processor for 5 min — mostly because of Chrome",
            AlertKinds.Check("cpu", Minute(95), new("Chrome", 40, 0), null, 90, 5, true));
    }

    [Fact]
    public void MemoryHighIsNotCritical()
    {
        var l = Minute(10, 2, s => s.Pressure = 2);
        Assert.Null(AlertKinds.Check("mem", l, null, null, 4, 2, true));
        Assert.NotNull(AlertKinds.Check("mem", l, null, null, 2, 2, true));
        Assert.Equal("Your memory is full — Edge is using 2.5 GB", AlertKinds.Check("mem", l, null, new("Edge", 1, 2.5e9), 2, 2, true));
    }

    [Fact]
    public void NetNeedsPingAndSaysOfflineWhenNoAnswer()
    {
        var l = Minute(10);   // PingMs NaN = no answers
        Assert.Null(AlertKinds.Check("net", l, null, null, 300, 3, false));
        Assert.Equal("No internet for 3 min", AlertKinds.Check("net", l, null, null, 300, 3, true));
        var slow = Minute(10, 2, s => s.PingMs = 340);
        Assert.Equal("Internet has been slow for 3 min (340 ms)", AlertKinds.Check("net", slow, null, null, 300, 3, true));
        Assert.Null(AlertKinds.Check("net", Minute(10, 2, s => s.PingMs = 20), null, null, 300, 3, true));
    }

    [Fact]
    public void BatteryOnlyWhenNotCharging_DiskOnlyWhenKnown_HeatNeedsATemperature()
    {
        Assert.NotNull(AlertKinds.Check("battery", Minute(1, 2, s => s.Battery = 15), null, null, 20, 1, true));
        Assert.Null(AlertKinds.Check("battery", Minute(1, 2, s => { s.Battery = 15; s.Charging = true; }), null, null, 20, 1, true));
        Assert.Null(AlertKinds.Check("battery", Minute(1), null, null, 20, 1, true));   // no battery
        Assert.Equal("Only 5 GB free on your SSD", AlertKinds.Check("disk", Minute(1, 2, s => s.DiskFree = 5e9), null, null, 10, 1, true));
        Assert.Null(AlertKinds.Check("disk", Minute(1), null, null, 10, 1, true));      // unknown (NaN) is not "0 GB free"
        Assert.Null(AlertKinds.Check("heat", Minute(99), null, null, 100, 5, true));    // no temperature on this PC
        Assert.NotNull(AlertKinds.Check("heat", Minute(10, 2, s => s.TempC = 103), null, null, 100, 5, true));
    }

    [Fact]
    public void AppHoggingUsesTheTopApp()
    {
        Assert.Null(AlertKinds.Check("app", Minute(10), null, null, 50, 10, true));
        Assert.Equal("Zoom has been using 55% processor for 10 min", AlertKinds.Check("app", Minute(10), new("Zoom", 55, 0), null, 50, 10, true));
    }

    [Fact]
    public void ExampleIsJustOverTheLimit()
    {
        Assert.Equal("Your PC has been at 92% processor for 5 min", AlertKinds.Example("cpu", 90, 5));
        Assert.Equal("Your PC has been at 100% processor for 5 min", AlertKinds.Example("cpu", 100, 5));
        Assert.Equal("Safari has been using 55% processor for 10 min", AlertKinds.Example("app", 50, 10));
        Assert.Equal("Only 7 GB free on your SSD", AlertKinds.Example("disk", 10, 1));
        foreach (var id in AlertKinds.Ids) Assert.False(string.IsNullOrEmpty(AlertKinds.Example(id, PrefTable.Alerts.First(a => a.Id == id).Limit, 3)), id);
    }

    [Fact]
    public void StreakMustReachMinutesThenFiresOnce()
    {
        var e = new AlertEngine();
        var s = Only("cpu", 90, 3);
        Assert.Empty(e.Evaluate(In(Minute(95), T0), s));
        Assert.Empty(e.Evaluate(In(Minute(95), T0.AddMinutes(1)), s));
        var f = e.Evaluate(In(Minute(95), T0.AddMinutes(2)), s);
        Assert.Single(f);
        Assert.Equal("Processor busy", f[0].Title);
        Assert.Empty(e.Evaluate(In(Minute(95), T0.AddMinutes(3)), s));   // cooldown 30 min
    }

    [Fact]
    public void AQuietMinuteResetsTheStreak_AndCooldownEnds()
    {
        var e = new AlertEngine();
        var s = Only("cpu", 90, 2);
        e.Evaluate(In(Minute(95), T0), s);
        e.Evaluate(In(Minute(10), T0.AddMinutes(1)), s);
        Assert.Equal(0, e.Streak("cpu"));
        Assert.Empty(e.Evaluate(In(Minute(95), T0.AddMinutes(2)), s));
        Assert.Single(e.Evaluate(In(Minute(95), T0.AddMinutes(3)), s));
        Assert.Empty(e.Evaluate(In(Minute(95), T0.AddMinutes(20)), s));
        Assert.Single(e.Evaluate(In(Minute(95), T0.AddMinutes(34)), s));
    }

    [Fact]
    public void TooFewSamplesOrNotifyOffResetEverything()
    {
        var e = new AlertEngine();
        var s = Only("cpu", 90, 2);
        e.Evaluate(In(Minute(95), T0), s);
        Assert.Equal(1, e.Streak("cpu"));
        Assert.Empty(e.Evaluate(In(Minute(95).Take(10).ToList(), T0.AddMinutes(1)), s));   // 10 × 2 s = 20 s < 40 s
        Assert.Equal(0, e.Streak("cpu"));
        e.Evaluate(In(Minute(95), T0.AddMinutes(2)), s);
        Assert.Empty(e.Evaluate(In(Minute(95), T0.AddMinutes(3), notify: false), s));
        Assert.Equal(0, e.Streak("cpu"));
        // Low impact: 8 samples × 5 s = 40 s is enough
        Assert.Single(e.Evaluate(new(Minute(95, 5).Take(8).ToList(), 5, true, true, 30, T0.AddMinutes(4), null, null), Only("cpu", 90, 1)));
    }

    [Fact]
    public void DiskCooldownIsADay_OffKindsNeverFire()
    {
        var e = new AlertEngine();
        var disk = Only("disk", 10, 1);
        List<LiveSample> Low() => Minute(1, 2, s => s.DiskFree = 5e9);
        Assert.Single(e.Evaluate(In(Low(), T0, cooldown: 5), disk));
        Assert.Empty(e.Evaluate(In(Low(), T0.AddHours(23), cooldown: 5), disk));
        Assert.Single(e.Evaluate(In(Low(), T0.AddHours(25), cooldown: 5), disk));
        Assert.Empty(e.Evaluate(In(Minute(99), T0.AddDays(3)), [new("cpu", false, 90, 1)]));
    }

    [Fact]
    public void RangesAndDescriptionsMatchTheMac()
    {
        Assert.Equal((2.0, 4.0, 2.0), AlertKinds.Range("mem"));
        Assert.Equal((1.0, 200.0, 1.0), AlertKinds.Range("disk"));
        Assert.Equal("pressure critical", AlertKinds.Describe("mem", 4));
        Assert.Equal("pressure high", AlertKinds.Describe("mem", 2));
        Assert.Equal("above 90%", AlertKinds.Describe("cpu", 90));
        Assert.Equal("20% or less", AlertKinds.Describe("battery", 20));
        Assert.Equal("above 300 ms or offline", AlertKinds.Describe("net", 300));
        foreach (var a in PrefTable.Alerts)
        {
            var (lo, hi, _) = AlertKinds.Range(a.Id);
            Assert.InRange(a.Limit, lo, hi);
            Assert.True(PrefTable.Known(AlertKinds.Key(a.Id, "limit")));
        }
    }
}

public class HotkeyAndTextTests
{
    [Fact]
    public void RecorderRules()
    {
        Assert.Equal(RecordResult.Cancel, HotkeyLogic.Record(0x1B, false, false, false).Result);
        Assert.Equal(RecordResult.Off, HotkeyLogic.Record(0x2E, false, false, false).Result);
        Assert.Equal(-1, HotkeyLogic.Record(0x08, true, true, false).Code);
        Assert.Equal(RecordResult.Ignore, HotkeyLogic.Record(0x11, true, false, false).Result);   // Ctrl alone: keep waiting
        Assert.Equal(RecordResult.Reject, HotkeyLogic.Record(0x46, false, false, true).Result);   // Shift+F needs Ctrl or Alt
        Assert.Equal(RecordResult.Reject, HotkeyLogic.Record(0x90, true, true, false).Result);    // NumLock is no shortcut key
        var ok = HotkeyLogic.Record(0x46, true, true, false);
        Assert.Equal((RecordResult.Accept, 0x46, 3, "Ctrl+Alt+F"), ok);
        Assert.Equal("Ctrl+Alt+Shift+F5", HotkeyLogic.Record(0x74, true, true, true).Label);
        Assert.Equal("Alt+Space", HotkeyLogic.Label(0x20, HotkeyLogic.Alt));
        Assert.Equal("Ctrl+Alt+Shift+Win+K", HotkeyLogic.Label(0x4B, 15));
    }

    [Fact]
    public void DefaultLabelIsWhatTheTableStores()
    {
        var p = new PrefStore(null);
        Assert.Equal(p.String("hotkeyLabel"), HotkeyLogic.Label(p.Int("hotkeyCode"), p.Int("hotkeyMods")));
    }

    [Fact]
    public void ProfileChangesLines()
    {
        string C(string id) => SettingsText.Changes(Profile.All.First(p => p.Id == id));
        Assert.Equal("Liquid Glass · large text · CPU RAM SSD BAT · no graphs · advice line · tray icon", C("simple"));
        Assert.Equal("Instrument · CPU GPU RAM NET · Mbit/s · tray compact", C("gamer"));
        Assert.Equal("Terminal · CPU GPU RAM NET SSD I/O BAT · real values · tray graphs", C("nerd"));
        Assert.Equal("Minimal light · BAT CPU RAM SSD · tray text · battery alert", C("laptop"));
    }

    [Fact]
    public void TextSizeNamesAndSummaries()
    {
        Assert.Equal("normal", SettingsText.TextSize(1.0));
        Assert.Equal("large", SettingsText.TextSize(1.15));
        Assert.Equal("extra large", SettingsText.TextSize(1.3));
        Assert.Equal("custom", SettingsText.TextSize(0.9));
        Assert.Equal("CPU, GPU, RAM", SettingsText.MetricList("cpu,gpu,ram,bogus"));
        Assert.Equal(4, SettingsText.KindsOn(k => new PrefStore(null).Bool(k)));   // defaults: cpu, heat, mem, disk
    }

    [Fact]
    public void TildeReplacesOnlyTheHomeFolder()
    {
        Assert.Equal(@"~\Documents\Fusebox", SettingsText.Tilde(@"C:\Users\me\Documents\Fusebox", @"C:\Users\me"));
        Assert.Equal(@"C:\Users\meer\x", SettingsText.Tilde(@"C:\Users\meer\x", @"C:\Users\me"));
        Assert.Equal(@"D:\logs", SettingsText.Tilde(@"D:\logs", @"C:\Users\me"));
    }
}

public class SelfLogTests
{
    static SelfRow Row(DateTime t, double cpu, bool visible = true, string page = "Live", string win = "300x340") =>
        new(t, "0.1 (202611212130)", cpu, 80, double.NaN, double.NaN, double.NaN, double.NaN, page, win, visible, "instrument", "style", "auto", "ac");

    [Fact]
    public void LineRoundTrips()
    {
        var t = new DateTime(2026, 11, 21, 12, 30, 0);
        var line = SelfLog.Line(Row(t, 0.456));
        Assert.Equal("2026-11-21 12:30:00,0.1 (202611212130),0.46,80,,,,,Live,300x340,1,instrument,style,auto,ac\n", line);
        var back = SelfLog.Parse(SelfLog.Header + line + "garbage\n");
        Assert.Single(back);
        Assert.Equal(0.46, back[0].Cpu);
        Assert.True(double.IsNaN(back[0].Watt));
        Assert.True(back[0].Visible);
    }

    [Fact]
    public void SizeBuckets()
    {
        Assert.Equal("small", SelfLog.SizeBucket("300x340"));
        Assert.Equal("medium", SelfLog.SizeBucket("700x700"));
        Assert.Equal("large", SelfLog.SizeBucket("1200x800"));
        Assert.Equal("-", SelfLog.SizeBucket("-"));
    }

    [Fact]
    public void ReportGroupsBySituationAndStaysAnonymous()
    {
        var t = new DateTime(2026, 11, 21, 12, 0, 0);
        var rows = Enumerable.Range(0, 10).Select(i => Row(t.AddMinutes(i), i + 1)).Append(Row(t.AddHours(1), 0.1, visible: false, win: "-")).ToList();
        var r = SelfLog.Report(rows, "0.1 (1)", "Windows 11 · 8 cores", "Right now: 1.0% CPU");
        Assert.StartsWith("Fusebox performance report", r);
        Assert.Contains("visible|Live|small|instrument|low:auto", r);
        Assert.Contains("hidden|Live|-|instrument|low:auto", r);
        Assert.Contains("11 minutes measured", r);
        Assert.Contains("Right now: 1.0% CPU", r);
        Assert.Equal(10, SelfLog.Percentile(Enumerable.Range(1, 10).Select(i => (double)i).ToList(), 0.95));
    }
}
