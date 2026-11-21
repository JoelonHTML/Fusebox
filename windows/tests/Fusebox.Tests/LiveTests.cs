using Fusebox.Core;

public class LiveTests
{
    static readonly DateTime T0 = new(2026, 1, 5, 10, 0, 0);

    [Fact]
    public void PeakNeedsTwoReadingsAndSurvivesOneDip()
    {
        var t = new PeakTracker(PeakKind.Cpu);
        Assert.True(t.Feed(T0, 90, 80).Started);
        t.Feed(T0.AddSeconds(2), 95, 80);
        Assert.Null(t.Feed(T0.AddSeconds(4), 10, 80).Closed);   // one dip: still open
        t.Feed(T0.AddSeconds(6), 85, 80);
        t.Feed(T0.AddSeconds(8), 10, 80);
        var closed = t.Feed(T0.AddSeconds(10), 10, 80).Closed;
        Assert.NotNull(closed);
        Assert.Equal(95, closed!.Top);
        Assert.Equal(T0.AddSeconds(2), closed.At);
        Assert.Equal(8, closed.Seconds);
    }

    [Fact]
    public void ShortSpikeIsNoPeak()
    {
        var t = new PeakTracker(PeakKind.Gpu);
        t.Feed(T0, 99, 80);
        t.Feed(T0.AddSeconds(2), 1, 80);
        Assert.Null(t.Feed(T0.AddSeconds(4), 1, 80).Closed);
    }

    [Fact]
    public void PeakWatchCollectsClosedPeaks()
    {
        var w = new PeakWatch();
        int raised = 0; w.Closed += _ => raised++;
        for (int i = 0; i < 4; i++) w.Feed(new LiveSample { Time = T0.AddSeconds(2 * i), Cpu = 95, RamUsed = 1, RamTotal = 100 });
        for (int i = 4; i < 7; i++) w.Feed(new LiveSample { Time = T0.AddSeconds(2 * i), Cpu = 5, RamUsed = 1, RamTotal = 100 });
        Assert.Equal(1, raised);
        Assert.Single(w.Today);
        Assert.Contains(",cpu,95.0,", PeakWatch.CsvLine(w.Today[0]));
    }

    [Fact]
    public void SeriesAveragesTodayPerMinute()
    {
        var s = new MetricSeries();
        s.Add(T0, 10); s.Add(T0.AddSeconds(2), 20);          // minute 10:00 -> 15
        s.Add(T0.AddMinutes(1), 40);                          // running minute
        var today = s.Values(Period.Today, 2);
        Assert.Equal([15.0, 40.0], today);
        Assert.Equal(3, s.Values(Period.Minutes5, 2).Count);
        var (min, avg, max) = MetricSeries.Stats(s.Values(Period.Hour, 2));
        Assert.Equal((10, 70.0 / 3, 40), (min, avg, max));
    }

    [Fact]
    public void FiveMinutesIsFewerPointsAtLowImpact()
    {
        var s = new MetricSeries();
        for (int i = 0; i < 200; i++) s.Add(T0.AddSeconds(i), i);
        Assert.Equal(60, s.Values(Period.Minutes5, 5).Count);
        Assert.Equal(150, s.Values(Period.Minutes5, 2).Count);
    }

    [Fact]
    public void CsvRoundTripsMinuteAverages()
    {
        var a = CsvLog.Line(new LiveSample { Time = T0, Cpu = 10 });
        var b = CsvLog.Line(new LiveSample { Time = T0.AddSeconds(2), Cpu = 20 });
        var c = CsvLog.Line(new LiveSample { Time = T0.AddMinutes(1), Cpu = 50, Battery = 80 });
        Assert.Equal(CsvLog.Header.Split(',').Length, a.TrimEnd('\n').Split(',').Length);
        var csv = CsvLog.Header + a + b + c;
        Assert.Equal([15.0, 50.0], CsvLog.MinuteAverages(csv, "cpu_pct"));
        Assert.Equal([80.0], CsvLog.MinuteAverages(csv, "batterij_pct"));
        Assert.Empty(CsvLog.MinuteAverages(csv, "nope"));
    }

    [Fact]
    public void BatteryTimeToFull()
    {
        var e = new BatteryEta();
        e.Add(T0, 30);
        Assert.True(double.IsNaN(e.ToFull(T0.AddMinutes(2), 32)));      // too soon
        Assert.Equal(60, e.ToFull(T0.AddMinutes(10), 40));              // 1%/min
        Assert.True(double.IsNaN(e.ToFull(T0.AddMinutes(10), 30)));     // not rising
        Assert.Equal("1 h 20 min", BatteryEta.Duration(80));
    }

    [Fact]
    public void MetricOrderParses()
    {
        Assert.Equal([Metric.Ram, Metric.Cpu], Metrics.Parse("ram, cpu,ram,bogus"));
        Assert.Equal("ram,cpu", Metrics.Join([Metric.Ram, Metric.Cpu]));
    }
}
