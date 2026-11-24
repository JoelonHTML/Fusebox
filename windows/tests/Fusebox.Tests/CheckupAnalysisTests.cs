using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Core.Processes;

namespace Fusebox.Tests;

public class CheckupAnalysisTests
{
    static readonly DateTime Day = new(2026, 11, 21);

    static CsvLog.Minute Min(DateTime t, double cpu = 10, int pressure = 1, double temp = double.NaN, double free = 200, double total = 500,
        double ping = 20, bool offline = false, bool drop = false, double wifi = double.NaN) =>
        new(t, cpu, 8, pressure, temp, free, total, ping, offline, drop, wifi, 30);

    static AppsLog.Row Row(DateTime t, string app, double cpu, double mem = 0.5, string kind = "app") => new(t, app, kind, cpu, mem, 0, 0, 0);

    static Report Build(List<CsvLog.Minute> minutes, List<AppsLog.Row>? apps = null, Period p = Period.Today, int cores = 4, CrashSummary? crashes = null) =>
        Analysis.Build(p, Day.AddHours(23), [new DayData(Day, minutes, apps ?? [], [], [])], [], cores, crashes);

    // ---- score (§5.5) ----

    [Fact]
    public void Score_follows_the_formula()
    {
        var calm = HealthScore.Of(new Counts(100, 0, 0, 0, 0, 0), false);
        Assert.Equal(10, calm.Score);
        Assert.StartsWith("Calm", calm.Reason);
        var busy = HealthScore.Of(new Counts(100, 50, 0, 0, 0, 0), false);   // 50 % → 10 → capped at 4
        Assert.Equal(6, busy.Score);
        Assert.Equal("50% of the time the CPU was busy (≥ 85%)", busy.Reason);
        Assert.Equal(1, HealthScore.Of(new Counts(100, 100, 100, 100, 0, 0), true).Score);      // 4+3+2+1 = 10 → max(1, 0)
        var disk = HealthScore.Of(new Counts(100, 0, 0, 0, 0, 0), true);
        Assert.Equal(9, disk.Score);
        Assert.Equal("Your disk is almost full", disk.Reason);
        var none = HealthScore.Of(new Counts(0, 0, 0, 0, 0, 0), false);
        Assert.Null(none.Score);
        Assert.Equal("No measurements for this period yet", none.Reason);
        Assert.Equal("Calm: not busy, memory and temperature fine", HealthScore.Of(new Counts(1000, 1, 0, 0, 0, 0), false).Reason);   // 0.02 < 0.25
    }

    [Fact]
    public void Score_levels_and_comparison()
    {
        Assert.Equal(0, HealthScore.Level(8)); Assert.Equal(1, HealthScore.Level(7)); Assert.Equal(1, HealthScore.Level(5));
        Assert.Equal(2, HealthScore.Level(4)); Assert.Null(HealthScore.Level(null));
        Assert.Equal("↑ better than yesterday (6)", HealthScore.Compare(8, 6, "yesterday"));
        Assert.Equal("↓ worse than yesterday (9)", HealthScore.Compare(8, 9, "yesterday"));
        Assert.Equal("= same as yesterday", HealthScore.Compare(8, 8, "yesterday"));
        Assert.Equal("", HealthScore.Compare(8, null, "yesterday"));
    }

    [Fact]
    public void Counts_use_the_constants()
    {
        var c = Counts.Of([Min(Day, cpu: 85), Min(Day.AddMinutes(1), cpu: 84.9), Min(Day.AddMinutes(2), pressure: 2, temp: 95, ping: 200, wifi: -76)]);
        Assert.Equal(new Counts(3, 1, 1, 1, 1, 1), c);
    }

    // ---- report ----

    [Fact]
    public void Busy_moments_name_their_culprit_and_gaps_break_a_run()
    {
        var minutes = new List<CsvLog.Minute>();
        var apps = new List<AppsLog.Row>();
        for (int i = 0; i < 6; i++)
        {
            var t = Day.AddHours(14).AddMinutes(i);
            minutes.Add(Min(t, 95));
            apps.Add(Row(t, "Xcode", 70)); apps.Add(Row(t, "Mail", 5)); apps.Add(Row(t, "Windows", 90, kind: "macos"));
        }
        for (int i = 0; i < 4; i++) minutes.Add(Min(Day.AddHours(16).AddMinutes(i), 95));           // only 4: not a busy moment
        minutes.Add(Min(Day.AddHours(18), 95)); minutes.Add(Min(Day.AddHours(18).AddMinutes(2), 95)); // a 2-minute gap breaks the run
        var r = Build(minutes, apps);
        var b = Assert.Single(r.Busy);
        Assert.Equal("Xcode", b.App);
        Assert.Equal(Day.AddHours(14).AddMinutes(6), b.End);
        Assert.Equal(12, r.BusyMin);   // every minute at or above 85 %
        Assert.Contains(r.Events, e => e.Text.StartsWith("Busy: 95% CPU") && e.Text.EndsWith("mostly Xcode"));
    }

    [Fact]
    public void Top_lists_need_solid_apps_and_windows_is_not_a_seen_active_app()
    {
        var minutes = Enumerable.Range(0, 100).Select(i => Min(Day.AddMinutes(i))).ToList();
        var apps = new List<AppsLog.Row>();
        for (int i = 0; i < 100; i++) { apps.Add(Row(Day.AddMinutes(i), "Chrome", 20, 3)); apps.Add(Row(Day.AddMinutes(i), "Windows", 30, 1, "macos")); }
        apps.Add(Row(Day.AddMinutes(3), "Flash", 99));   // seen once: below min(5, 100/20 = 5)
        var r = Build(minutes, apps);
        Assert.Equal(["Windows", "Chrome"], r.TopCpu.Select(a => a.Name));
        Assert.DoesNotContain(r.TopCpu, a => a.Name == "Flash");
        Assert.Equal("Chrome", r.TopRam[0].Name);
        Assert.DoesNotContain(r.SeenLongest, a => a.Name == "Windows");
        Assert.Equal(100, r.TopCpu[1].Minutes);
        Assert.Equal(20, r.TopCpu[1].Cpu);
    }

    [Fact]
    public void Same_name_twice_in_a_minute_adds_up()
    {
        var apps = new List<AppsLog.Row> { Row(Day, "Search", 10), Row(Day, "Search", 5), Row(Day.AddMinutes(1), "Search", 15) };
        var r = Build([Min(Day), Min(Day.AddMinutes(1))], apps);
        Assert.Equal(15, r.All.Single().Cpu);
        Assert.Equal(2, r.All.Single().Minutes);
    }

    [Fact]
    public void Runaway_is_a_full_core_for_ten_minutes_and_housekeeping_is_a_job()
    {
        var minutes = new List<CsvLog.Minute>(); var apps = new List<AppsLog.Row>();
        for (int i = 0; i < 12; i++)
        {
            var t = Day.AddHours(9).AddMinutes(i);
            minutes.Add(Min(t, 40));
            apps.Add(Row(t, "Handbrake", 26));                      // 4 cores → a full core is 25 %
            apps.Add(Row(t, "Microsoft Windows Search Indexer", 8));   // 8 % × 4 = 32 % of a core: busy
        }
        var r = Build(minutes, apps, cores: 4);
        var run = Assert.Single(r.Runaways);
        Assert.Equal(("Handbrake", 12), (run.App, run.Minutes));
        Assert.Equal("Windows Search is indexing", Assert.Single(r.Jobs).Job);
        Assert.Equal(12, r.Jobs[0].Minutes);
        Assert.Contains(r.Events, e => e.Text == "Windows Search is indexing — passes by itself");
        Assert.Contains(r.Events, e => e.Text.StartsWith("Handbrake used a full processor core"));
        Assert.Empty(Build(minutes.Take(9).ToList(), apps.Where(a => a.Time < Day.AddHours(9).AddMinutes(9)).ToList(), cores: 4).Runaways);
    }

    [Fact]
    public void A_leak_needs_growth_time_and_a_straight_line()
    {
        var growing = Enumerable.Range(0, 240).Select(i => ("Slack", Day.AddMinutes(i), 1.0 + i * 0.02)).ToList();   // 1.0 → 5.78 GB in 4 h
        var leak = Assert.Single(Analysis.Leaks(growing));
        Assert.Equal("Slack", leak.App);
        Assert.Equal(4, leak.Hours);
        Assert.True(leak.PerHour > 1);
        Assert.Empty(Analysis.Leaks(Enumerable.Range(0, 240).Select(i => ("Flat", Day.AddMinutes(i), 2.0)).ToList()));
        Assert.Empty(Analysis.Leaks(Enumerable.Range(0, 100).Select(i => ("Short", Day.AddMinutes(i), 1.0 + i * 0.1)).ToList()));          // under 3 h
        Assert.Empty(Analysis.Leaks(Enumerable.Range(0, 240).Select(i => ("Jumpy", Day.AddMinutes(i), i % 20 < 10 ? 1.0 : 4.0)).ToList())); // not a straight line
    }

    [Fact]
    public void Disk_internet_and_memory_runs_become_events()
    {
        var minutes = new List<CsvLog.Minute>();
        for (int i = 0; i < 6; i++) minutes.Add(Min(Day.AddMinutes(i), pressure: i == 2 ? 4 : 2, free: 40, ping: double.NaN, offline: true));
        var r = Build(minutes);
        Assert.True(r.DiskLow);
        Assert.Contains(r.Events, e => e.Text == "Memory full" && e.Level == 2);
        Assert.Contains(r.Events, e => e.Text == "Internet down");
        Assert.Contains(r.Events, e => e.Text == "Disk almost full: 40 GB free");
        Assert.Equal(6, r.PressureMin);
        Assert.Equal(2, Build([Min(Day, drop: true), Min(Day.AddMinutes(1), drop: true), Min(Day.AddMinutes(30), drop: true)]).WifiDrops);
    }

    [Fact]
    public void Previous_period_gives_the_comparison_score()
    {
        var today = new DayData(Day, Enumerable.Range(0, 20).Select(i => Min(Day.AddMinutes(i))).ToList(), [], [], []);
        var yesterday = new DayData(Day.AddDays(-1), Enumerable.Range(0, 20).Select(i => Min(Day.AddDays(-1).AddMinutes(i), 95)).ToList(), [], [], []);
        var r = Analysis.Build(Period.Today, Day.AddHours(5), [today], [yesterday], 4);
        Assert.Equal(10, r.Score);
        Assert.Equal(6, r.PrevScore);
        Assert.Equal("↑ better than yesterday (6)", HealthScore.Compare(r.Score, r.PrevScore, Periods.PreviousName(r.Period)));
    }

    [Fact]
    public void Periods_cover_the_right_days()
    {
        Assert.Equal([0], Periods.Days(Period.Today)); Assert.Equal([1], Periods.PrevDays(Period.Today));
        Assert.Equal([1], Periods.Days(Period.Yesterday)); Assert.Equal([2], Periods.PrevDays(Period.Yesterday));
        Assert.Equal(7, Periods.Days(Period.Week).Length); Assert.Equal(13, Periods.PrevDays(Period.Week).Max());
        Assert.Equal(Period.Week, Periods.Parse("week", Period.Today)); Assert.Equal(Period.Yesterday, Periods.Parse("last", Period.Yesterday));
        var (from, to) = Periods.Range(Period.Week, Day.AddHours(10));
        Assert.Equal((Day.AddDays(-6), Day.AddDays(1)), (from, to));
    }

    [Fact]
    public void Day_files_are_read_and_cached()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusebox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(CsvLog.FileFor(Day, "meterkast", dir), CsvLog.Header + "2026-11-21 10:00:00,50.0,50.0,8.0,1,0,,,0,0,20.0,100.0,,500.0,0,0,0.00,,\n2026-11-21 10:00:02,60.0,50.0,8.0,1,0,,,0,0,20.0,100.0,,500.0,0,0,0.00,,\nbroken\n");
            File.WriteAllText(CsvLog.FileFor(Day, "apps", dir), "2026-11-21 10:00:00,Chrome,app,12.0,1.5,0\n");   // old file without header
            var d = DayLoader.Load(Day, dir);
            Assert.Single(d.Minutes);
            Assert.Equal(55, d.Minutes[0].Cpu);
            Assert.Equal("Chrome", Assert.Single(d.Apps).App);
            Assert.Equal(2, d.Files.Count);
            Assert.Same(DayLoader.Load(Day, dir).Minutes, d.Minutes);   // unchanged file: same parsed list
            Assert.Empty(DayLoader.Load(Day.AddDays(-1), dir).Minutes);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- crashes ----

    static EventInfo Ev(string provider, int id, DateTime t, string? app = null) => new(t, provider, id, app == null ? [] : [(null, app)]);

    [Fact]
    public void Crash_loops_and_counts()
    {
        var evs = new List<EventInfo>();
        for (int i = 0; i < 5; i++) evs.Add(Ev("Application Error", 1000, Day.AddMinutes(i * 10), "bad.exe"));   // 5 within 1 h
        evs.Add(Ev("Application Error", 1000, Day.AddHours(5), "Other.exe")); evs.Add(Ev("Application Error", 1000, Day.AddHours(9), "Other.exe"));
        evs.Add(Ev("Application Hang", 1002, Day.AddHours(1), "Hung.exe"));
        evs.Add(Ev("Microsoft-Windows-Resource-Exhaustion-Detector", 2004, Day.AddHours(2)));
        evs.Add(Ev("Microsoft-Windows-WER-SystemErrorReporting", 1001, Day.AddHours(3)));
        evs.Add(Ev("Microsoft-Windows-Kernel-Power", 41, Day.AddHours(3)));
        evs.Add(Ev("Application Error", 1000, Day.AddDays(-3), "Old.exe"));
        var s = CrashAnalysis.Build(evs, Day, Day.AddDays(1));
        Assert.Equal(("bad", 5, true), (s.Apps[0].App, s.Apps[0].Crashes, s.Apps[0].Loop));
        Assert.Equal(3, s.Apps.Count);
        Assert.DoesNotContain(s.Apps, a => a.App == "Old");
        Assert.Equal((1, 1, 1), (s.OutOfMemory, s.BlueScreens, s.UnexpectedShutdowns));
        Assert.Equal((2, 1), (s.Crashed, s.Hung));
        Assert.Equal(2, CrashAnalysis.MaxInHour([Day, Day.AddMinutes(59), Day.AddMinutes(130)]));
        Assert.False(CrashSummary.None.Any);
    }

    // ---- tips (§5.6.1) ----

    [Fact]
    public void Tips_come_in_order_with_login_last_and_serious_flags()
    {
        var r = new Report
        {
            Period = Period.Today, DiskFreeGb = 30, DiskTotalGb = 500, PressureMin = 45, WifiDrops = 3,
            Leaks = [new("Slack", 1, 5, 4, Day, Day.AddHours(4))],
            Crashes = new([new("bad", 6, 0, 6), new("Meh", 2, 0, 2), new("Hang", 0, 3, 0)], 2, 1, 0),
            All = [new("Chrome", 5, 3.2, 100, 10, 4), new("Slack", 2, 5, 100, 5, 6)],
        };
        var tips = Tips.Build(r, new() { UptimeDays = 20, PendingReboot = true, Battery = new(70, 400), Stoppable = a => a == "Chrome" });
        Assert.Equal("login", tips[^1].Id);
        Assert.Equal(["leakSlack", "appChrome", "loopbad", "crashedMeh", "stopped respondingHang", "jetsam", "bluescreen", "battery", "restart", "uptime", "disk", "wifidrop", "mem", "login"],
            tips.Select(t => t.Id).ToList());
        Assert.True(tips.First(t => t.Id == "restart").Serious);
        Assert.Equal("ms-settings:windowsupdate", tips.First(t => t.Id == "restart").Action!.Uri);
        Assert.False(tips.First(t => t.Id == "appChrome").Serious);
        Assert.Contains("close tabs", tips.First(t => t.Id == "appChrome").Text);
        Assert.Equal("Chrome", tips.First(t => t.Id == "appChrome").Action!.StopApp);
        Assert.Null(tips.First(t => t.Id == "leakSlack").Action);     // not running / not stoppable here
        Assert.Equal(["stability", "power", "perf", "storage"], new[] { "loopbad", "battery", "uptime", "disk" }.Select(id => tips.First(t => t.Id == id).Group));
        Assert.Equal("storage", tips[^1].Group);
        Assert.Equal("ms-settings:startupapps", tips[^1].Action!.Uri);
    }

    [Fact]
    public void Calm_report_only_has_the_login_tip_and_restart_can_be_off()
    {
        var tips = Tips.Build(new Report(), new() { UptimeDays = 90, RestartDays = 0 });
        Assert.Equal(["login"], tips.Select(t => t.Id));
    }

    [Fact]
    public void Tip_text_splits_into_head_and_rest()
    {
        Assert.Equal(("Chrome uses 3.2 GB on average", "Close tabs you're not using"), new CheckupTip("a", "Chrome uses 3.2 GB on average — close tabs you're not using", false).Split());
        Assert.Equal(("Not restarted for 20 days.", "A restart clears memory."), new CheckupTip("b", "Not restarted for 20 days. A restart clears memory.", false).Split());
        Assert.Equal(("No split here", ""), new CheckupTip("c", "No split here", false).Split());
    }

    [Fact]
    public void Gist_takes_the_first_serious_tip_else_not_login()
    {
        var r = new Report { Score = 8, Reason = "Calm" };
        var g = CheckupGist.From(r, [new("app1", "Chrome uses 3 GB — close tabs", false), new("disk", "Your disk is almost full (10 GB free) — clean up", true), new("login", "Check", false)]);
        Assert.Equal(("Your disk is almost full (10 GB free)", true, 8), (g.TipHead, g.TipSerious, g.Score));
        Assert.Equal("Chrome uses 3 GB", CheckupGist.From(r, [new("app1", "Chrome uses 3 GB — close tabs", false)]).TipHead);
        Assert.Null(CheckupGist.From(r, [new("login", "Check", false)]).TipHead);
    }

    // ---- diagnose (§5.6.2) ----

    static DiagInput Diag(double cpu = 10) => new() { Cpu = cpu, Apps = [new("Chrome", false, 55, 2.5), new("Windows", true, 20, 1)], Now = Day };

    [Fact]
    public void Diagnose_names_the_culprit_and_stops_at_two_sentences()
    {
        var d = Diagnose.Run(new DiagInput { Cpu = 92, Pressure = 4, TempC = 99, PingMs = 300, Apps = Diag().Apps, Now = Day });
        Assert.Equal("Chrome", d.Culprit);
        Assert.Equal("The CPU is at 92%, mostly because of Chrome (55%). Memory is full.", d.Text);   // already the culprit → no "— Chrome is using"
        var m = Diagnose.Run(new DiagInput { Cpu = 20, Pressure = 2, Apps = Diag().Apps, Now = Day });
        Assert.Equal("Memory is filling up — Chrome is using 2.5 GB.", m.Text);
        Assert.Equal("Chrome", m.Culprit);
    }

    [Fact]
    public void Diagnose_windows_itself_and_nothing_wrong()
    {
        var w = Diagnose.Run(new DiagInput { Cpu = 90, Apps = [new("Windows", true, 60, 1)], JobsNow = ["Windows Search is indexing"], Now = Day });
        Assert.Equal("The CPU is at 90%, mostly because of Windows itself: Windows Search is indexing — this passes by itself, no action needed.", w.Text);
        Assert.Null(w.Culprit);
        var ok = Diagnose.Run(new DiagInput { Cpu = 12, Apps = [new("Chrome", false, 3, 1)], TempC = 52, Now = Day });
        Assert.Equal("Nothing wrong: CPU 12%, memory normal, 52 °C. Biggest user: Chrome.", ok.Text);
        Assert.Null(ok.Culprit);
        Assert.True(Diagnose.Run(new DiagInput { Cpu = 5, Now = Day }).NoAppData);
        var slow = Diagnose.Run(new DiagInput { Cpu = 5, DiskFreeGb = 10, DiskTotalGb = 500, PingMs = 150, UptimeDays = 20, Now = Day });
        Assert.Equal("Your disk is almost full (10 GB free). Internet is responding slowly (150 ms) — that's probably not your PC.", slow.Text);
    }

    [Fact]
    public void Diagnose_recent_runaway_and_leak()
    {
        var rep = new Report { Runaways = [new("Handbrake", 30, Day.AddHours(-1), Day.AddMinutes(-2))], Leaks = [new("Slack", 1, 5, 4, Day.AddHours(-5), Day.AddMinutes(-10))] };
        var d = Diagnose.Run(new DiagInput { Cpu = 40, Report = rep, Apps = [new("Slack", false, 1, 5)], Now = Day });
        Assert.StartsWith("Handbrake has been using a full processor core for 30 min.", d.Text);
        Assert.Contains("Slack grew from 1.0 to 5.0 GB in 4 h", d.Text);
        Assert.Equal("Handbrake", d.Culprit);
    }

    // ---- housekeeping ----

    [Fact]
    public void Housekeeping_job_matching()
    {
        Assert.Equal("Microsoft Defender is scanning", Housekeeping.JobFor("Antimalware Service Executable"));
        Assert.Equal("OneDrive is syncing", Housekeeping.JobFor("Microsoft OneDrive"));
        Assert.Equal("Windows Update is installing updates", Housekeeping.JobFor("TiWorker"));
        Assert.Null(Housekeeping.JobFor("Chrome"));
        Assert.Equal(["Windows Search is indexing"], Housekeeping.Busy([("SearchIndexer", 10), ("OneDrive", 1)], 4));
    }
}
