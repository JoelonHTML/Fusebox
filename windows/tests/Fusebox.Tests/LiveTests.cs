using Fusebox.Core.Processes;
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
        w.Today[0].App = "Big, App"; w.Today[0].AppValue = 61;
        var line = PeakWatch.CsvLine(w.Today[0]);
        Assert.Equal("2026-01-05 10:00:00,2026-01-05 10:00:08,cpu,95.0,Big  App,61.0\n", line);
        var back = PeakWatch.Parse(PeakWatch.CsvHeader + line + "garbage\n");
        Assert.Single(back);
        Assert.Equal((PeakKind.Cpu, 95.0, "Big  App", 8), (back[0].Kind, back[0].Top, back[0].App, back[0].Seconds));
        Assert.Single(PeakWatch.Parse("time,end,kind,top,app\n2026-01-05 10:00:00,2026-01-05 10:00:08,gpu,88.0,\n"));   // v1 file
    }

    [Fact]
    public void MemoryPeakNeedsOneSampleOfHighPressure()
    {
        var w = new PeakWatch();
        w.Feed(new LiveSample { Time = T0, Pressure = 2 });
        w.Feed(new LiveSample { Time = T0.AddSeconds(2), Pressure = 1 });
        w.Feed(new LiveSample { Time = T0.AddSeconds(4), Pressure = 1 });
        Assert.Equal(PeakKind.Mem, Assert.Single(w.Today).Kind);
    }

    static LiveSample S(DateTime t, double cpu) => new() { Time = t, Cpu = cpu, Pressure = cpu > 50 ? 2 : 1 };

    [Fact]
    public void HistoryAveragesTodayPerMinute()
    {
        var h = new SampleHistory();
        h.Add(S(T0, 10)); h.Add(S(T0.AddSeconds(2), 20));        // minute 10:00 → 15
        h.Add(S(T0.AddMinutes(1), 60));                          // running minute
        Assert.Equal([15.0, 60.0], Metrics.Values(h.Today, Metric.Cpu));
        Assert.Equal(2, h.Today[^1].Pressure);
        Assert.Equal(3, h.Range(TimeRange.Hour, 5, 2).Count);
        var (min, avg, max) = Metrics.Stats(Metrics.Values(h.Recent, Metric.Cpu));
        Assert.Equal((10, 30, 60), (min, avg, max));
    }

    [Fact]
    public void RecentKeepsOneHourAndAtMost1800()
    {
        var h = new SampleHistory();
        for (int i = 0; i < 2000; i++) h.Add(S(T0.AddSeconds(i), i % 100));
        Assert.Equal(SampleHistory.RecentCap, h.Recent.Count);
        var slow = new SampleHistory();
        for (int i = 0; i < 1000; i++) slow.Add(S(T0.AddSeconds(5 * i), 1));
        Assert.Equal(721, slow.Recent.Count);                      // 3600 s at 5 s, both ends
        Assert.Equal(61, slow.Since(300, 5).Count);                // 5 min with half a step margin: no flicker
        Assert.Equal(61, slow.Range(TimeRange.Short, 5, 5).Count);
        Assert.Equal(13, slow.Range(TimeRange.Short, 0, 5).Count); // shortMinutes clamped to 1
    }

    [Fact]
    public void SeedKeepsOnlyEarlierMinutesOfToday()
    {
        var h = new SampleHistory();
        var csv = CsvLog.Header + CsvLog.Line(S(T0, 10)) + CsvLog.Line(S(T0.AddSeconds(2), 30)) + CsvLog.Line(S(T0.AddMinutes(1), 50))
                  + CsvLog.Line(S(T0.AddMinutes(-1).AddDays(-1), 99));
        h.Seed(SampleHistory.MinuteAverages(CsvLog.ReadSamples(csv)), T0.AddMinutes(1).AddSeconds(30));
        h.Add(S(T0.AddMinutes(1).AddSeconds(30), 70));
        Assert.Equal([20.0, 70.0], Metrics.Values(h.Today, Metric.Cpu));   // the CSV's running minute is replaced by live samples
    }

    [Fact]
    public void MainCsvHasMacColumnsAndReadsBack()
    {
        var s = new LiveSample
        {
            Time = T0, Cpu = 12.34, RamUsed = 8 * 1073741824.0, RamTotal = 16 * 1073741824.0, Gpu = double.NaN, NetIn = 1500.7, NetOut = -3,
            DiskFree = 412e9, DiskTotal = 994e9, DiskRead = 1e6, DiskWrite = 2e6, Pressure = 2, WifiDbm = -52,
        };
        var line = CsvLog.Line(s);
        Assert.Equal("2026-01-05 10:00:00,12.3,50.0,8.0,2,0.0,,,1500,0,,412.0,,994.0,1000000,2000000,0.00,,-52\n", line);
        Assert.Equal(CsvLog.Header.Split(',').Length, line.Split(',').Length);
        var back = Assert.Single(CsvLog.ReadSamples(CsvLog.Header + line + "nonsense,row\n"));
        Assert.Equal(12.3, back.Cpu);
        Assert.Equal(50.0, back.RamPercent, 3);
        Assert.Equal(2, back.Pressure);
        Assert.True(double.IsNaN(back.Battery));
        Assert.Equal(-52, back.WifiDbm);
    }

    [Fact]
    public void CheckupMinutes()
    {
        string Row(int sec, string cpu, string ping, string wifi) => $"2026-01-05 10:00:{sec:00},{cpu},50.0,8.0,{(sec == 4 ? 4 : 1)},0.0,,,0,0,{ping},400.0,,900.0,0,0,0.00,,{wifi}\n";
        var csv = CsvLog.Header + Row(0, "10.0", "", "0") + Row(2, "30.0", "20.0", "-60") + Row(4, "nan", "", "") + Row(6, "20.0", "", "-70")
                  + "2026-01-05 10:01:00,5.0\n";
        var m = CsvLog.ReadMinutes(csv);
        Assert.Equal(2, m.Count);
        Assert.Equal(20.0, m[0].Cpu);
        Assert.Equal(1, m[0].Pressure);         // the row with pressure 4 had no CPU value: skipped
        Assert.True(m[0].Offline);              // 1 of 3 samples answered
        Assert.True(m[0].WifiDrop);
        Assert.Equal(-65, m[0].Wifi);
        Assert.Equal(400.0, m[0].DiskFreeGb);
        Assert.Equal(5.0, m[1].Cpu);
    }

    [Fact]
    public void HeaderMigrationRemapsByName()
    {
        var v1 = "time,end,kind,top,app\n2026-01-05 10:00:00,2026-01-05 10:00:08,cpu,95.0,X\n";
        var m = CsvLog.Remap(v1, PeakWatch.CsvHeader, PeakWatch.V1Columns);
        Assert.Equal(PeakWatch.CsvHeader + "2026-01-05 10:00:00,2026-01-05 10:00:08,cpu,95.0,X,\n", m);
        Assert.Null(CsvLog.Remap(m!, PeakWatch.CsvHeader));                    // already current
        Assert.Null(CsvLog.Remap("a,b\n1,2\n", PeakWatch.CsvHeader));          // unknown file: left alone
    }

    [Fact]
    public void RetentionUsesTheFileNameDate()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fusebox-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in new[] { "meterkast-2026-01-01.csv", "apps-2026-01-01.csv", "peaks-2026-01-10.csv", "self-2025-12-01.csv", "battery-health.csv", "notes.csv" })
                File.WriteAllText(Path.Combine(dir, f), "");
            Assert.Equal(3, CsvLog.Prune(30, new DateTime(2026, 2, 5, 9, 0, 0), dir));
            Assert.Equal(["battery-health.csv", "notes.csv", "peaks-2026-01-10.csv"], Directory.EnumerateFiles(dir).Select(Path.GetFileName).Order());
            Assert.Equal(0, CsvLog.Prune(0, new DateTime(2030, 1, 1), dir));
            CsvLog.DeleteAll(dir);   // only Fusebox day files
            Assert.Equal(["battery-health.csv", "notes.csv"], Directory.EnumerateFiles(dir).Select(Path.GetFileName).Order());
            File.Delete(Path.Combine(dir, CsvLog.BatteryFile));
            Assert.True(CsvLog.AppendBatteryHealth(T0, 91.26, 312, dir));
            Assert.False(CsvLog.AppendBatteryHealth(T0.AddHours(3), 91, 312, dir));
            Assert.Equal(CsvLog.BatteryHeader + "2026-01-05,91.3,312\n", File.ReadAllText(Path.Combine(dir, CsvLog.BatteryFile)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AppsLogPicksTopAndOpenApps()
    {
        var groups = Enumerable.Range(0, 30).Select(i => new AppGroup
        {
            Key = $"k{i}", Name = i == 0 ? "Visual Studio, Code" : $"P{i}", Kind = i == 15 ? AppKind.App : AppKind.Background,
            Cpu = 30 - i, Ram = (long)(i * 1073741824.0 / 10),
        }).ToList();
        var lines = AppsLog.Lines(T0, groups).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(21, lines.Length);   // top 10 CPU (P0–P9) + top 10 memory (P20–P29) + the open app P15
        Assert.Equal("2026-01-05 10:00:00,Visual Studio  Code,achtergrond,30.0,0.00,0.00,0.0,0.00", lines[0]);
        var back = AppsLog.Parse(AppsLog.Header + string.Join('\n', lines));
        Assert.Equal(21, back.Count);
        Assert.Single(back, r => r.Kind == "app");
        Assert.Single(AppsLog.Parse("2026-01-05 10:00:00,Old,app,1.0,0.50,0.00\n"));   // no header: fixed Mac order
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
        Assert.Equal([Metric.Ram, Metric.Cpu, Metric.Battery], Metrics.Parse("ram, cpu,ram,bogus,bat,battery,1"));
        Assert.Equal("ram,cpu", Metrics.Join([Metric.Ram, Metric.Cpu]));
    }
}
