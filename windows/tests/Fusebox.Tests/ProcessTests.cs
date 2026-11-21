using Fusebox.Core;
using Fusebox.Core.Processes;

public class ProcessTests
{
    static ProcSample P(int pid, int parent, string name, long created = 100, long ram = 1000) => new(pid, parent, name, 0, created, ram, 2);

    static List<AppGroup> Run(ProcSample[] procs, int[] windows, Dictionary<int, double>? cpu = null, Dictionary<int, double>? gpu = null, int self = -1) =>
        AppGrouper.Group(procs, new HashSet<int>(windows), pid => $@"C:\x\{procs.First(p => p.Pid == pid).Name}",
            (path, img) => AppGrouper.Stem(img).ToUpperInvariant(), cpu ?? [], gpu ?? [], self);

    [Fact]
    public void BrowserHelpersRollUpIntoTheWindowedApp()
    {
        var g = Run([P(4, 0, "System"), P(10, 1, "explorer.exe", 50), P(20, 10, "chrome.exe", 100), P(21, 20, "chrome.exe", 110), P(22, 21, "gpu-helper.exe", 120), P(30, 10, "notepad.exe", 100)],
            [10, 20, 30], new() { [20] = 5, [21] = 3, [22] = 2 });
        var chrome = g.Single(x => x.Name == "CHROME");
        Assert.Equal(AppKind.App, chrome.Kind);
        Assert.Equal(3, chrome.Pids.Count);
        Assert.Equal(10, chrome.Cpu, 6);
        Assert.Contains(g, x => x.Name == "NOTEPAD" && x.Kind == AppKind.App);   // child of explorer is NOT absorbed by explorer
        Assert.Contains(g, x => x.ImageName == "explorer");
    }

    [Fact]
    public void SystemProcessesFormOneUnstoppableGroup_BackgroundIsSeparate()
    {
        var g = Run([P(0, 0, "System Idle Process"), P(4, 0, "System"), P(300, 4, "csrss.exe"), P(800, 300, "svchost.exe"), P(900, 800, "svchost.exe"), P(1000, 1, "updater.exe")], [], self: 1000);
        var win = g.Single(x => x.Kind == AppKind.Windows);
        Assert.Equal(4, win.Pids.Count);
        Assert.False(win.CanStop);
        var upd = g.Single(x => x.Name == "UPDATER");
        Assert.Equal(AppKind.Background, upd.Kind);
        Assert.False(upd.CanStop);   // own pid
    }

    [Fact]
    public void SameExeWithoutParentLinkIsOneApp_AndPidReuseIsIgnored()
    {
        var g = Run([P(10, 1, "app.exe", 100), P(11, 1, "app.exe", 100), P(12, 999, "child.exe", 100)], [10]);
        Assert.Equal(2, g.Single(x => x.Name == "APP").Pids.Count);
        // parent 20 is newer than its "child" 21 -> pid reuse, not absorbed
        var g2 = Run([P(20, 1, "a.exe", 500), P(21, 20, "b.exe", 100)], [20]);
        Assert.Equal(2, g2.Count);
    }

    [Fact]
    public void SortsByColumn()
    {
        var a = new AppGroup { Name = "a", Cpu = 1, Ram = 50 };
        var b = new AppGroup { Name = "b", Cpu = 9, Ram = 10 };
        Assert.Equal("b", AppGrouper.Sort([a, b], AppGrouper.SortBy.Cpu)[0].Name);
        Assert.Equal("a", AppGrouper.Sort([a, b], AppGrouper.SortBy.Ram)[0].Name);
    }

    [Fact]
    public void CpuPercentMath()
    {
        // 1 s wall on 4 CPUs; process used 1 s of CPU = 25% of the PC
        Assert.Equal(25, CpuTracker.Percent(10_000_000, 10_000_000, 4), 6);
        Assert.Equal(100, CpuTracker.Percent(99_000_000, 10_000_000, 4));   // clamped
        Assert.Equal(0, CpuTracker.Percent(5, 0, 4));
        var t = new CpuTracker();
        var first = t.Update([new(7, 1, "x", 0, 1, 0, 1)], 0, 2);
        Assert.Equal(0, first[7]);
        var second = t.Update([new(7, 1, "x", 5_000_000, 1, 0, 1), new(8, 1, "y", 9, 2, 0, 1)], 10_000_000, 2);
        Assert.Equal(25, second[7], 6);
        Assert.Equal(0, second[8]);
    }

    [Fact]
    public void ParsesGpuInstanceNames()
    {
        Assert.True(GpuInstance.TryParse("pid_1234_luid_0x00000000_0x0000A1B2_phys_0_eng_0_engtype_3D", out var pid, out var type));
        Assert.Equal(1234, pid);
        Assert.Equal("3D", type);
        Assert.False(GpuInstance.TryParse("luid_0x0_eng_0", out _, out _));
        var per = GpuInstance.PerPid([
            ("pid_5_luid_0_phys_0_eng_0_engtype_3D", 10), ("pid_5_luid_0_phys_0_eng_1_engtype_3D", 15),
            ("pid_5_luid_0_phys_0_eng_2_engtype_Copy", 20), ("pid_6_luid_0_phys_0_eng_0_engtype_VideoDecode", 3)]);
        Assert.Equal(25, per[5], 6);   // sum of 3D engines (25) beats Copy (20)
        Assert.Equal(3, per[6], 6);
    }

    [Fact]
    public void SearchMatchesNameOrExplanation_InstalledPrefixFirst()
    {
        Assert.True(ProcessList.MatchesSearch("Google Chrome", "", "chrome"));
        Assert.True(ProcessList.MatchesSearch("Windows Search", "Indexes your files", "index"));
        Assert.False(ProcessList.MatchesSearch("Google Chrome", "", "firefox"));
        Assert.True(ProcessList.MatchesSearch("x", "", ""));
        InstalledApp A(string n) => new(n, null);
        var l = ProcessList.Installed([A("Firefox"), A("My Fire Tools"), A("Chrome"), A("firefox"), A("Fire Tools")], ["Chrome"], "fire");
        Assert.Equal(["Fire Tools", "Firefox", "My Fire Tools"], l.Select(a => a.Name));
        Assert.Empty(ProcessList.Installed([A("A")], [], " "));
        Assert.Equal(30, ProcessList.Installed(Enumerable.Range(0, 80).Select(i => A("app" + i)), [], "app").Count);
    }

    [Fact]
    public void ChipFilterAndSmallItems()
    {
        var app = new AppGroup { Kind = AppKind.App };
        var bg = new AppGroup { Kind = AppKind.Background, Cpu = 0.01, Ram = 5_000_000 };
        var win = new AppGroup { Kind = AppKind.Windows };
        Assert.True(ProcessList.MatchesFilter(app, "apps"));
        Assert.False(ProcessList.MatchesFilter(bg, "apps"));
        Assert.True(ProcessList.MatchesFilter(win, "macos"));
        Assert.True(ProcessList.MatchesFilter(bg, "all"));
        Assert.True(ProcessList.IsSmall(bg, "idle"));
        Assert.False(ProcessList.IsSmall(bg, "all"));
        Assert.False(ProcessList.IsSmall(win, "light"));   // the leftover row stays
        Assert.False(ProcessList.IsSmall(new AppGroup { Cpu = 0.01, Ram = 25 * 1048576 }, "idle"));   // memory reaches its limit
        Assert.True(ProcessList.IsSmall(new AppGroup { Cpu = 0.5, Ram = 100 * 1048576 }, "light"));
        Assert.False(ProcessList.IsSmall(new AppGroup { Cpu = 1, Ram = 1 }, "light"));
    }

    [Fact]
    public void ColumnsFollowWidthBandsAndHiddenIds()
    {
        string Ids(double w, params string[] hide) => string.Join(",", ProcessList.Columns(w, hide).Select(c => c.Col));
        Assert.Equal("Cpu,Ram", Ids(400));
        Assert.Equal("Cpu,Ram,Disk", Ids(460));
        Assert.Equal("Cpu,Gpu,Ram,Disk", Ids(560));
        Assert.Equal("Cpu,Gpu,Ram,Disk,Threads,Procs", Ids(700));
        Assert.Equal("Cpu,Gpu,Ram,Disk,Threads,Procs,Running", Ids(820));
        Assert.Equal("Cpu,Ram,Threads,Procs", Ids(900, "gpu", "disk", "running"));
        Assert.Equal(58, ProcessList.Columns(500, []).First().Width);
        Assert.Equal(90, ProcessList.Columns(700, []).First().Width);
    }

    [Fact]
    public void RemainderRowGetsWhatTheAppsDoNotAccountFor_NeverLess()
    {
        var win = new AppGroup { Kind = AppKind.Windows, Cpu = 1, Ram = 1_000 };
        var app = new AppGroup { Kind = AppKind.App, Cpu = 10, Ram = 4_000 };
        AppGrouper.AddRemainder([win, app], 30, 10_000);
        Assert.Equal(20, win.Cpu, 6);
        Assert.Equal(6_000, win.Ram);
        AppGrouper.AddRemainder([win, app], 5, 100);   // total below the sum: keep what was measured
        Assert.Equal(20, win.Cpu, 6);
        Assert.Equal(6_000, win.Ram);
    }

    [Fact]
    public void SortsByRunningTimeDiskAndThreads()
    {
        var old = new AppGroup { Name = "old", Started = 100, DiskRead = 5, Threads = 2 };
        var young = new AppGroup { Name = "young", Started = 900, DiskWrite = 50, Threads = 9 };
        var unknown = new AppGroup { Name = "unknown", DiskRead = 1 };
        Assert.Equal(["old", "young", "unknown"], AppGrouper.Sort([unknown, young, old], AppGrouper.SortBy.Running).Select(g => g.Name));
        Assert.Equal("young", AppGrouper.Sort([old, young, unknown], AppGrouper.SortBy.Disk)[0].Name);
        Assert.Equal("young", AppGrouper.Sort([old, young, unknown], AppGrouper.SortBy.Threads)[0].Name);
        // CPU ties go to the bigger memory
        Assert.Equal("b", AppGrouper.Sort([new AppGroup { Name = "a", Cpu = 1, Ram = 1 }, new AppGroup { Name = "b", Cpu = 1, Ram = 9 }], AppGrouper.SortBy.Cpu)[0].Name);
    }

    [Fact]
    public void DiskRatesNeedTwoSamplesAndIgnoreReusedPids()
    {
        var t = new DiskTracker();
        var first = t.Update([new(7, 1, "x", 0, 1, 0, 1, 1000, 2000)], 0);
        Assert.Equal((0, 0), first[7]);
        var second = t.Update([new(7, 1, "x", 0, 1, 0, 1, 3000, 2500), new(8, 1, "y", 0, 2, 0, 1, 9, 9)], 20_000_000);   // 2 s later
        Assert.Equal((1000, 250), second[7]);
        Assert.Equal((0, 0), second[8]);
        var third = t.Update([new(7, 1, "x", 0, 1, 0, 1, 10, 10)], 30_000_000);   // counter went down: treated as new
        Assert.Equal((0, 0), third[7]);
    }

    [Fact]
    public void GroupSumsDiskAndNamesParts()
    {
        var procs = new[] { new ProcSample(10, 1, "app.exe", 0, 100, 1000, 2), new ProcSample(11, 10, "helper.exe", 0, 110, 1000, 2) };
        var g = AppGrouper.Group(procs, new HashSet<int> { 10 }, _ => "app.exe", (_, img) => "App", new Dictionary<int, double>(), new Dictionary<int, double>(), -1,
            new Dictionary<int, (double, double)> { [10] = (100, 10), [11] = (50, 5) }).Single();
        Assert.Equal(150, g.DiskRead);
        Assert.Equal(15, g.DiskWrite);
        Assert.Equal(["app", "helper"], g.Parts);
    }

    [Fact]
    public void AppUsageFromLogRows()
    {
        var day = new DateTime(2026, 11, 20, 0, 0, 0);   // a Friday
        AppsLog.Row R(int h, int m, string app, double cpu, double gpu = 0) => new(day.AddHours(h).AddMinutes(m), app, "app", cpu, 0.5, 0, gpu, 0);
        var rows = new[] { R(9, 0, "Edge", 10), R(9, 1, "Edge", 30), R(9, 1, "Edge", 99) /* duplicate minute */, R(14, 0, "Edge", 20, 4), R(9, 0, "Other", 50), R(0, 0, "Edge", 5) };
        var u = AppUsage.Compute(rows, "Edge", day.AddHours(1));   // the 00:00 row is before "since"
        Assert.Equal(3, u.Minutes);
        Assert.Equal(20, u.AvgCpu, 6);
        Assert.Equal(30, u.PeakCpu);
        Assert.Equal(0.5, u.AvgMemGb, 6);
        Assert.True(u.HasGpu);
        Assert.Equal(2, u.ByHour[9]);
        Assert.Equal(1, u.ByHour[14]);
        Assert.Equal(3, u.ByWeekday[4]);   // Monday first: Friday = 4
        Assert.Equal(0, AppUsage.Compute(rows, "Nothing", day).Minutes);
        Assert.Equal(1, AppUsage.Compute([new AppsLog.Row(day, "A B", "app", 1, 0, 0, 0, 0)], "A,B", day).Minutes);   // commas were written as spaces
    }

    [Fact]
    public void AppUsageLoadReadsTheDaysFromDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusebox-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = new DateTime(2026, 11, 21, 12, 0, 0);
            string Day(DateTime d, string app) => AppsLog.Header + $"{d:yyyy-MM-dd} 10:00:00,{app},app,12.0,1.00,0.00,0.0,0.00\n";
            File.WriteAllText(CsvLog.FileFor(now, "apps", dir), Day(now, "Edge"));
            File.WriteAllText(CsvLog.FileFor(now.AddDays(-3), "apps", dir), Day(now.AddDays(-3), "Edge"));
            File.WriteAllText(CsvLog.FileFor(now.AddDays(-10), "apps", dir), Day(now.AddDays(-10), "Edge"));
            Assert.Equal(1, AppUsage.Load("Edge", 1, now, dir).Minutes);
            Assert.Equal(2, AppUsage.Load("Edge", 7, now, dir).Minutes);
            Assert.Equal(3, AppUsage.Load("Edge", 30, now, dir).Minutes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FriendlyNamesAreOurOwn()
    {
        Fusebox.Core.Strings.Load("en");
        Assert.Equal("Windows Search", KnownProcesses.FriendlyName("SearchIndexer"));
        Assert.True(KnownProcesses.IsOwnName(KnownProcesses.WindowsName));
        Assert.False(KnownProcesses.IsOwnName("Notepad"));
        var g = Run([P(4, 0, "System"), P(50, 4, "SearchIndexer.exe")], []).Single(x => x.ImageName == "SearchIndexer");
        Assert.Equal("Windows Search", g.Name);
        Assert.Equal("Windows itself", Run([P(4, 0, "System")], []).Single().Name);
    }

    [Fact]
    public void HistoryKeepsAveragePeakAndPrunes()
    {
        var h = new AppHistory();
        var t = DateTime.UtcNow;
        h.Record([new AppGroup { Key = "k", Cpu = 10, Ram = 100 }], t);
        h.Record([new AppGroup { Key = "k", Cpu = 30, Ram = 300 }], t.AddSeconds(2));
        var s = h.Get("k")!;
        Assert.Equal(20, s.CpuAvg, 6);
        Assert.Equal(30, s.CpuPeak);
        Assert.Equal(2, s.Cpu.Count);
        h.Record([new AppGroup { Key = "other" }], t.AddHours(2));
        Assert.Null(h.Get("k"));
    }

    [Fact]
    public void KnowsCommonProcesses() => Assert.NotNull(KnownProcesses.Explain("MsMpEng"));
}

public class ProcessTextTests
{
    [Fact]
    public void FormatsAndJudges()
    {
        Fusebox.Core.Strings.Load("en");
        Assert.Equal("0.4%", ProcessText.Pct(0.4));
        Assert.Equal("37%", ProcessText.Pct(37));
        Assert.Equal("1h 5m", ProcessText.Duration(65));
        Assert.Equal("5m", ProcessText.Duration(5));
        Assert.Equal("3d 4h", ProcessText.Duration(3 * 1440 + 4 * 60));
        Assert.Equal("1.2 GB", ProcessText.Mem(1.2 * 1073741824));
        Assert.Equal("340 MB", ProcessText.Mem(340 * 1048576.0));
        var g = new AppGroup { Name = "X", Cpu = 60, Pids = [1, 2] };
        Assert.Equal(ProcessText.Verdict.HeavyCpu, ProcessText.Judge(g, 16e9));
        var lines = ProcessText.Lines(g, 8, 16 * 1073741824.0, DateTime.Now);
        Assert.Contains("X is using 60% of your processor right now (≈ 4.8 of the 8 cores) and 0 MB of memory (0.0% of your 16 GB).", lines);
        Assert.Contains("It consists of 2 parts (processes).", lines);
        g.Parts.AddRange(["a", "b"]);
        Assert.Contains("It consists of 2 parts (processes), including a, b.", ProcessText.Lines(g, 8, 16 * 1073741824.0, DateTime.Now));
    }
}
