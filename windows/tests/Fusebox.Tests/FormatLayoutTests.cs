using Fusebox.Core;
using Fusebox.Core.Look;

public class FormatTests
{
    [Fact]
    public void MacNumberRules()
    {
        Assert.Equal("23%", Fmt.Pct(23.4));
        Assert.Equal("0%", Fmt.Pct(-0.4));            // never "−0%"
        Assert.Equal("1%", Fmt.Pct(0.5));
        Assert.Equal("–", Fmt.Pct(double.NaN));
        Assert.Equal("12.4", Fmt.One(12.44));
        Assert.Equal("640 KB", Fmt.Bytes(640_999));   // truncated
        Assert.Equal("12.3 MB", Fmt.Bytes(12.3e6));
        Assert.Equal("1.2 GB", Fmt.Bytes(1.2e9));
        Assert.Equal("640 KB/s", Fmt.Rate(640e3));
        Assert.Equal("5.1 Mbit/s", Fmt.Short(637_500, bits: true));
        Assert.Equal("640 kbit/s", Fmt.Short(80_000, bits: true));
        Assert.Equal("42°", Fmt.Deg(41.6));
        Assert.Equal("", Fmt.Deg(double.NaN));
        Assert.Equal("48m", Fmt.Duration(48));
        Assert.Equal("2h 35m", Fmt.Duration(155));
        Assert.Equal("1d 3h", Fmt.Duration(1440 + 190));
        Assert.Equal("2:35", Fmt.BatteryTime(155));
        Assert.Equal("14h", Fmt.BatteryTime(14 * 60 + 20));
        Assert.Equal("", Fmt.BatteryTime(double.NaN));
        Assert.Equal("1,850", Fmt.Rpm(1850));
        Assert.Equal(2, Fmt.Level(95, 85, 95));
        Assert.Equal(1, Fmt.Level(85, 85, 95));
        Assert.Equal(0, Fmt.Level(84.9, 85, 95));
    }

    [Fact]
    public void RatePairSharesOneUnit()
    {
        Assert.Equal(("8", "120", "KB/s"), Fmt.RatePair(8_000, 120_000));
        Assert.Equal(("1.2", "34", "MB/s"), Fmt.RatePair(1.2e6, 34e6));
        Assert.Equal(("0", "5", "KB/s"), Fmt.RatePair(-100, 5_000));
        Assert.Equal(("9.6", "0.0", "Mbit/s"), Fmt.RatePair(1.2e6, 0, bits: true));
        Assert.Equal(("2.0", "12", "GB/s"), Fmt.RatePair(2e9, 12e9));
    }

    [Fact]
    public void StatusSentenceFirstMatchWins()
    {
        Strings.Load("en");
        Assert.Equal(("Memory is full — close a few apps", 2), StatusSentence.Of(4, 120, 99));
        Assert.Equal(("Your PC is very hot — it's slowing itself down", 2), StatusSentence.Of(1, 105, 10));
        Assert.Equal(("Your PC is busy", 1), StatusSentence.Of(2, double.NaN, 85));
        Assert.Equal(("Memory is filling up", 1), StatusSentence.Of(2, double.NaN, 10));
        Assert.Equal(("Your PC is getting warm", 1), StatusSentence.Of(1, 95, 10));
        Assert.Equal(("All calm", 0), StatusSentence.Of(1, double.NaN, double.NaN));
        Assert.Equal(4, MemoryPressure.FromRamPercent(96));
        Assert.Equal(2, MemoryPressure.FromRamPercent(91));
        Assert.Equal(1, MemoryPressure.FromRamPercent(90));
    }
}

public class LayoutTests
{
    static Tier T(double h, double pad, double w = 300) => LiveLayout.Compute(w, h, 6, pad, false).Tier;

    [Fact]
    public void TierBandsMatchRowHeights()   // the Mac's LiveLayout self-test
    {
        foreach (var pad in new double[] { 10, 15, 22 })
        {
            Assert.Equal(Tier.S, T(6 * (36 + pad) + 32, pad));
            Assert.Equal(Tier.XS, T(6 * (19 + pad / 2) + 10, pad));
            Assert.Equal(Tier.M, T(6 * (100 + pad) + 58, pad));
            Assert.Equal(Tier.L, T(6 * (160 + pad) + 58, pad));
            Assert.Equal(Tier.S, T(6 * (38 + pad) + 10, pad));
            Assert.Equal(Tier.XS, T(6 * (38 + pad) + 9, pad));
        }
    }

    [Fact]
    public void ColumnsAndXl()
    {
        var small = LiveLayout.Compute(300, 340, 6, 15, false);
        Assert.Equal((1, 6), (small.Cols, small.Rows));
        var five = LiveLayout.Compute(774, 400, 5, 15, false);
        Assert.Equal((3, 2), (five.Cols, five.Rows));
        Assert.Equal(new LiveLayout(Tier.XL, 3, 2, false), LiveLayout.Compute(1000, 700, 6, 15, false));
        Assert.True(LiveLayout.Compute(1630, 730, 5, 22, false).Side);
        Assert.Equal(1, LiveLayout.Compute(300, 340, 0, 15, false).Rows);
    }

    [Fact]
    public void AdviceLineCostsSpaceAboveXs()
    {
        double h = 6 * (38 + 15) + 10;
        Assert.Equal(Tier.S, LiveLayout.Compute(300, h, 6, 15, false).Tier);
        Assert.Equal(Tier.XS, LiveLayout.Compute(300, h, 6, 15, true).Tier);
    }
}

public class PlacementTests
{
    [Fact]
    public void FitShiftsInsteadOfShrinking()   // spec §2.3 vector, converted to y-down (Windows): Mac top T → y = 800 − T
    {
        var screen = new Box(0, 0, 1440, 800);
        Assert.Equal(new Box(840, 100, 600, 700), Placement.Fit(new Box(1300, 500, 600, 700), screen));
        Assert.Equal(new Box(0, 0, 1440, 800), Placement.Fit(new Box(0, 10, 2000, 1200), screen));
        Assert.Equal(new Box(10, 10, 316, 160), Placement.Fit(new Box(10, 10, 100, 50), screen));
    }

    [Fact]
    public void SettingsGoesRightThenLeftThenCenter()   // spec §9.1 self-test values
    {
        var screen = new Box(0, 0, 1710, 1070);
        Assert.Equal(363, Placement.Settings(new Box(35, 100, 316, 340), screen, 740, 600).X);
        Assert.Equal(548, Placement.Settings(new Box(1300, 100, 400, 340), screen, 740, 600).X);
        Assert.Equal(485, Placement.Settings(new Box(0, 0, 1710, 1070), screen, 740, 600).X);
        Assert.Equal(100, Placement.Settings(new Box(35, 100, 316, 340), screen, 740, 600).Y);
        Assert.Equal(470, Placement.Settings(new Box(35, 900, 316, 100), screen, 740, 600).Y);   // moved up to fit
    }

    [Fact]
    public void SavedTopLeftOnlyWhenOnAScreen()
    {
        Box[] screens = [new(0, 0, 1920, 1040), new(1920, 0, 1280, 984)];
        Assert.True(Placement.TopLeftVisible(100, 100, screens));
        Assert.True(Placement.TopLeftVisible(3000, 900, screens));
        Assert.False(Placement.TopLeftVisible(3190, 100, screens));
        Assert.False(Placement.TopLeftVisible(-500, 100, screens));
        Assert.Equal((460, 420), Placement.GrowTo((300, 340), (460, 420)));
        Assert.Equal((800, 600), Placement.GrowTo((800, 600), (460, 420)));
    }
}
