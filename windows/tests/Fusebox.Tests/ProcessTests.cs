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
        Assert.Contains(g, x => x.Name == "EXPLORER");
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
    public void SearchFilter()
    {
        Assert.True(ProcessSearch.Matches("Google Chrome", "chro goo"));
        Assert.False(ProcessSearch.Matches("Google Chrome", "firefox"));
        Assert.True(ProcessSearch.Matches("x", ""));
        var l = ProcessSearch.NotRunning(["Firefox", "Fire Tools", "Chrome", "firefox"], ["Chrome"], "fire");
        Assert.Equal(["Fire Tools", "Firefox"], l);
        Assert.Empty(ProcessSearch.NotRunning(["A"], [], " "));
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
        Assert.Equal("1 h 5 min", ProcessText.Duration(65));
        var g = new AppGroup { Name = "X", Cpu = 60, Pids = [1, 2] };
        Assert.Equal(ProcessText.Verdict.HeavyCpu, ProcessText.Judge(g, 16e9));
        var lines = ProcessText.Lines(g, 8, 16e9, DateTime.Now);
        Assert.Contains("X is using 60% of your processor right now (≈ 4.8 of the 8 cores) and 0 B of memory (0.0% of your 16 GB).", lines);
        Assert.Contains("It consists of 2 parts (processes).", lines);
    }
}
