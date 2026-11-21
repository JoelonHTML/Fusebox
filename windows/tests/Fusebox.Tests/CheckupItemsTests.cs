using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Core.Processes;

namespace Fusebox.Tests;

public class CheckupItemsTests
{
    static readonly DateTime Day = new(2026, 11, 21);

    // ---- scrubber (§11.6) ----

    [Fact]
    public void Scrubber_removes_identity_paths_ips_and_macs()
    {
        var id = new ScrubIdentity("jdoe1", "Jane Doe", "JANES-LAPTOP", "CONTOSO");
        var text = @"Log in C:\Users\jdoe1\AppData\Local\Temp by Jane Doe on JANES-LAPTOP (CONTOSO) at 192.168.1.20 / 10.0.0.1, adapter 00-1A-2B-3C-4D-5E and a0:b1:c2:d3:e4:f5; jdoe1 again, D:/Users/someone/x.";
        var s = Scrubber.Scrub(text, id);
        Assert.Equal("Log in ~\\AppData\\Local\\Temp by … on … (…) at x.x.x.x / x.x.x.x, adapter xx:xx:xx:xx:xx:xx and xx:xx:xx:xx:xx:xx; … again, ~/x.", s);
        Assert.DoesNotContain("jdoe", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scrubber_keeps_generic_words_short_names_and_version_numbers()
    {
        var id = new ScrubIdentity("PC", "User", "Windows", "abc");
        var text = "PC User Windows abc, Windows 11 build 10.0.26100.4061, 1.2.3 and 999.1.1.1";
        Assert.Equal(text, Scrubber.Scrub(text, id));
        Assert.Equal("x.x.x.x", Scrubber.Scrub("255.255.255.0", new("", "", "", "")));
    }

    [Fact]
    public void Report_text_has_sections_and_can_be_scrubbed()
    {
        var r = new Report
        {
            Period = Period.Today, Score = 8, Reason = "Calm: not busy, memory and temperature fine", Minutes = 600, BusyMin = 12,
            Busy = [new(Day.AddHours(14), Day.AddHours(14).AddMinutes(10), 95, "Xcode")],
            TopCpu = [new("Chrome", 20, 3, 100, 50, 4)], TopRam = [new("Chrome", 20, 3.2, 100, 50, 4)],
            Crashes = new([new("bad", 3, 0, 0)], 0, 0, 0), Jobs = [new("OneDrive is syncing", 8)],
        };
        var text = ReportText.Build(r, new ReportInfo { Processor = "AMD Ryzen", MemoryGb = 16, Windows = "11 (build 26100)", UptimeMinutes = 3 * 1440 + 120, DiskTotalGb = 494, DiskFreeGb = 120, Battery = "92% of original capacity" },
            [new("login", "Check", false), new("disk", "Your disk is almost full — clean", true)], Day.AddHours(10));
        Assert.StartsWith("Fusebox Checkup report — Today\nCreated ".Replace("\n", Environment.NewLine), text);
        foreach (var h in new[] { "PC", "BATTERY", "HEALTH (today)", "BUSY MOMENTS", "TOP APPS", "CRASHES AND REPORTS FROM WINDOWS", "WINDOWS HOUSEKEEPING SEEN", "TIPS" })
            Assert.Contains(Environment.NewLine + h + Environment.NewLine, text);
        Assert.Contains("Score: 8/10 — Calm", text);
        Assert.Contains("- bad: crashed 3×", text);
        Assert.DoesNotContain("Check\r", text);
        Assert.DoesNotContain("FINDINGS", text);   // sections without data are omitted
    }

    [Fact]
    public void Csv_export_merges_days_in_the_current_column_order()
    {
        var current = CsvLog.Header + "2026-11-21 10:00:00,50.0,50.0,8.0,1,0,,,0,0,20.0,100.0,,500.0,0,0,0.00,,\n";
        var old = "tijd,cpu_pct,ram_gb\n2026-11-20 09:00:00,30.0,4.0\n";
        var merged = ReportText.MergeCsv([old, current]);
        var lines = merged.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(CsvLog.Header.TrimEnd('\n'), lines[0]);
        Assert.StartsWith("2026-11-20 09:00:00,30.0,,4.0,", lines[1]);
        Assert.Equal(lines[0].Split(',').Length, lines[1].Split(',').Length);
    }

    // ---- startup (§5.B.1) ----

    [Fact]
    public void Exe_paths_come_out_of_command_lines()
    {
        Func<string, string> same = s => s;
        Assert.Equal(@"C:\Program Files\Dropbox\Dropbox.exe", StartupParse.ExePath("\"C:\\Program Files\\Dropbox\\Dropbox.exe\" /home", same));
        Assert.Equal(@"C:\Program Files\Zoom\bin\Zoom.exe", StartupParse.ExePath(@"C:\Program Files\Zoom\bin\Zoom.exe --silent", same));
        Assert.Equal("rundll32", StartupParse.ExePath("rundll32 x.dll,Run", same));
        Assert.Equal(@"C:\Win\x.exe", StartupParse.ExePath(@"%SystemRoot%\x.exe", s => s.Replace("%SystemRoot%", @"C:\Win")));
        Assert.Null(StartupParse.ExePath("  ", same));
    }

    [Fact]
    public void Orphans_are_missing_absolute_local_paths_only()
    {
        Assert.True(StartupParse.IsOrphan(@"C:\gone\a.exe", _ => false));
        Assert.False(StartupParse.IsOrphan(@"C:\here\a.exe", _ => true));
        Assert.False(StartupParse.IsOrphan("rundll32", _ => false));
        Assert.False(StartupParse.IsOrphan(@"\\server\share\a.exe", _ => false));
        Assert.False(StartupParse.IsOrphan(null, _ => false));
    }

    [Fact]
    public void Friendly_names()
    {
        Assert.Equal("Dropbox", StartupParse.Friendly("Dropbox", null, null));
        Assert.Equal("Microsoft Edge", StartupParse.Friendly("MicrosoftEdgeAutoLaunch_5E0F1A2B3C4D5E6F7A8B9C0D", null, "Microsoft Edge"));
        Assert.Equal("MicrosoftEdgeAutoLaunch", StartupParse.Friendly("MicrosoftEdgeAutoLaunch_5E0F1A2B3C4D5E6F7A8B9C0D", null, null));
        Assert.Equal("updater", StartupParse.Friendly("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}", @"C:\x\updater.exe", null));
    }

    [Fact]
    public void Usage_text_has_no_watt_part()
    {
        Assert.Equal("0.4% CPU · 52 MB", StartupParse.Usage(0.4, 52));
        Assert.Equal("<0.1% CPU · 1.5 GB", StartupParse.Usage(0.04, 1536));
        Assert.Equal("12% CPU", StartupParse.Usage(12.4, double.NaN));
        Assert.Equal("", StartupParse.Usage(double.NaN, double.NaN));
        Assert.True(StartupParse.Heavy(5)); Assert.False(StartupParse.Heavy(4.9));
    }

    [Fact]
    public void Duplicates_merge_and_sort_by_cpu()
    {
        var items = new[]
        {
            new StartupItem { Name = "OneDrive", Scope = StartupScope.Machine, Kind = StartupKind.Run, Running = true, Cpu = 1, MemMb = 100 },
            new StartupItem { Name = "onedrive", Scope = StartupScope.User, Kind = StartupKind.Run, Id = "mine", Cpu = 2, MemMb = 50 },
            new StartupItem { Name = "Zoom", Kind = StartupKind.Run, Cpu = double.NaN },
            new StartupItem { Name = "Alpha", Kind = StartupKind.Run, Cpu = 0.5 },
        };
        var m = StartupParse.Merge(items);
        Assert.Equal(["onedrive", "Alpha", "Zoom"], m.Select(i => i.Name));   // the per-user item names the row
        Assert.Equal(("mine", true, 3, 150), (m[0].Id, m[0].Running, m[0].Cpu, m[0].MemMb));
        Assert.Equal(3, StartupParse.CountAtLogin(items.Append(new StartupItem { Name = "Svc", Kind = StartupKind.Service })));
        Assert.Equal(2, StartupParse.CountAtLogin(new[] { items[0], new StartupItem { Name = "Gone", Orphan = true }, new StartupItem { Name = "Off", Enabled = false } }.Append(items[2])));
    }

    [Fact]
    public void Toggle_rules()
    {
        Assert.True(new StartupItem { Kind = StartupKind.Run, Scope = StartupScope.User }.CanToggleDirect);
        Assert.True(new StartupItem { Kind = StartupKind.Folder, Scope = StartupScope.User }.CanToggleDirect);
        Assert.False(new StartupItem { Kind = StartupKind.Run, Scope = StartupScope.Machine }.CanToggleDirect);
        Assert.True(new StartupItem { Kind = StartupKind.Run, Scope = StartupScope.Machine }.CanToggleElevated);
        Assert.True(new StartupItem { Kind = StartupKind.Task }.CanToggleElevated);
        Assert.True(new StartupItem { Kind = StartupKind.Service }.CanToggle);
        Assert.False(new StartupItem { Kind = StartupKind.RunOnce, Scope = StartupScope.User }.CanToggle);
        Assert.False(new StartupItem { Kind = StartupKind.Service }.AtLogin);
    }

    const string TaskXml = @"<?xml version=""1.0"" ?>
<Tasks>
<Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task""><RegistrationInfo><URI>\Vendor\Updater</URI></RegistrationInfo>
<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers><Actions><Exec><Command>C:\v\up.exe</Command></Exec></Actions></Task>
<Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task""><RegistrationInfo><URI>\Microsoft\Windows\X\Y</URI></RegistrationInfo>
<Triggers><LogonTrigger/></Triggers><Actions><Exec><Command>C:\ms.exe</Command></Exec></Actions></Task>
<Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task""><RegistrationInfo><URI>\Off</URI></RegistrationInfo>
<Triggers><LogonTrigger/></Triggers><Settings><Enabled>false</Enabled></Settings><Actions><Exec><Command>C:\off.exe</Command></Exec></Actions></Task>
<Task xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task""><RegistrationInfo><URI>\Daily</URI></RegistrationInfo>
<Triggers><CalendarTrigger/></Triggers><Actions><Exec><Command>C:\d.exe</Command></Exec></Actions></Task>
</Tasks>";

    [Fact]
    public void Logon_tasks_outside_microsoft_are_found()
    {
        var t = StartupParse.ParseTasks(TaskXml);
        Assert.Equal(2, t.Count);
        Assert.Equal((@"\Vendor\Updater", "Updater", @"C:\v\up.exe", true), (t[0].Path, t[0].Name, t[0].Command, t[0].Enabled));
        Assert.False(t[1].Enabled);
        Assert.Empty(StartupParse.ParseTasks("garbage <Task broken"));
    }

    // ---- elevated scripts ----

    [Fact]
    public void Quote_cannot_be_broken_out_of()
    {
        Assert.Equal("'it''s'", ElevatedScripts.Quote("it's"));
        Assert.Equal("'a\u2019\u2019b'", ElevatedScripts.Quote("a\u2019b"));
        Assert.Equal("'x\u2018\u2018;calc;\u201A\u201A'", ElevatedScripts.Quote("x\u2018;calc;\u201A"));
        Assert.Equal("'ab'", ElevatedScripts.Quote("a\nb"));
    }

    [Fact]
    public void Toggle_scripts()
    {
        var run = ElevatedScripts.Toggle(StartupKind.Run, StartupScope.Machine, "Zoom", false)!;
        Assert.Contains(@"StartupApproved\Run'", run);
        Assert.Contains("-Name 'Zoom'", run);
        Assert.Contains("[byte[]]@(3,", run);
        Assert.Contains("[byte[]]@(2,0,0,0,0,0,0,0,0,0,0,0)", ElevatedScripts.Toggle(StartupKind.Run, StartupScope.Machine32, "Zoom", true)!);
        Assert.Contains(@"StartupApproved\Run32'", ElevatedScripts.Toggle(StartupKind.Run, StartupScope.Machine32, "Z", true)!);
        Assert.Contains(@"StartupApproved\StartupFolder'", ElevatedScripts.Toggle(StartupKind.Folder, StartupScope.Machine, "a.lnk", true)!);
        var task = ElevatedScripts.Toggle(StartupKind.Task, StartupScope.Machine, @"\Vendor\Updater", false)!;
        Assert.Contains("Disable-ScheduledTask -TaskPath '\\Vendor\\' -TaskName 'Updater'", task);
        Assert.Contains("Enable-ScheduledTask", ElevatedScripts.Toggle(StartupKind.Task, StartupScope.Machine, @"\Solo", true)!);
        Assert.Contains("Set-Service -Name 'Svc' -StartupType Manual", ElevatedScripts.Toggle(StartupKind.Service, StartupScope.Machine, "Svc", false)!);
        Assert.Contains("-StartupType Automatic", ElevatedScripts.Toggle(StartupKind.Service, StartupScope.Machine, "Svc", true)!);
        Assert.Null(ElevatedScripts.Toggle(StartupKind.Run, StartupScope.User, "x", false));   // per-user items never need elevation
        Assert.Null(ElevatedScripts.Toggle(StartupKind.RunOnce, StartupScope.Machine, "x", false));
        Assert.StartsWith("$env:PSModulePath", ElevatedScripts.Wrap("'ok'", "C:\\t\\r.txt"));
        Assert.Contains("$('ok') | Out-File -LiteralPath 'C:\\t\\r.txt'", ElevatedScripts.Wrap("'ok'", "C:\\t\\r.txt"));
    }

    // ---- disk advice (§5.B.2) ----

    [Fact]
    public void Disk_advice_by_path_then_name()
    {
        Assert.Equal(Verdict.Safe, DiskAdvice.For(@"AppData\Local\Temp")!.Value.V);
        Assert.Equal(Verdict.Safe, DiskAdvice.For(@"projects\web\node_modules")!.Value.V);
        Assert.Equal(Verdict.Safe, DiskAdvice.For(".nuget")!.Value.V);
        Assert.Equal(Verdict.Keep, DiskAdvice.For(@"AppData\Local\Packages")!.Value.V);
        Assert.Equal(Verdict.Careful, DiskAdvice.For("Downloads")!.Value.V);
        Assert.Equal(Verdict.Safe, DiskAdvice.For("Recycle Bin")!.Value.V);
        Assert.Null(DiskAdvice.For("MyStuff"));
        Assert.Equal(["can go", "check first", "keep", "?"], new[] { Verdict.Safe, Verdict.Careful, Verdict.Keep, Verdict.Unknown }.Select(DiskAdvice.Label));
        Assert.Equal("AppData › Local › Temp", DiskAdvice.Title(@"AppData\Local\Temp"));
        Assert.Equal("Downloads", DiskAdvice.Title("Downloads"));
    }

    [Fact]
    public void Biggest_inside_names_children_over_100_mb_with_their_verdict()
    {
        var row = new DiskRow("Dev", @"C:\Users\x\Dev", 20_000_000_000, [("node_modules", 12_000_000_000), ("src", 50_000_000), ("assets", 3_000_000_000), ("bin", 2_000_000_000), ("obj", 1_500_000_000)]);
        Assert.Equal("Biggest inside: node_modules 12.0 GB (can go) · assets 3.0 GB · bin 2.0 GB", DiskAdvice.BiggestInside(row, "Dev"));
        Assert.Null(DiskAdvice.BiggestInside(new DiskRow("x", "x", 1, [("a", 10)]), "x"));
    }

    // ---- power ----

    [Fact]
    public void Battery_report_xml_is_read_tolerantly_and_never_the_serial()
    {
        const string xml = @"<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012""><Batteries><Battery><Id>DELL 123</Id><SerialNumber>SECRET9</SerialNumber>
<DesignCapacity>50000</DesignCapacity><FullChargeCapacity>46000</FullChargeCapacity><CycleCount>312</CycleCount></Battery></Batteries>
<History><HistoryEntry LocalStartDate=""2026-10-01"" Days=""7"" DesignCapacity=""50000"" FullChargeCapacity=""48000"" CycleCount=""270""/>
<HistoryEntry LocalStartDate=""2026-11-01"" Days=""7"" DesignCapacity=""50000"" FullChargeCapacity=""46500"" CycleCount=""300""/></History></BatteryReport>";
        var (now, hist) = BatteryTrend.ParseReport(xml);
        Assert.Equal(new BatteryHealth(92, 312), now);
        Assert.Equal(2, hist.Count);
        Assert.Equal((96.0, 270), (hist[0].Pct, hist[0].Cycles));
        Assert.Equal("Since 2026-10-01: 96% → 93% · +30 cycles", BatteryTrend.Summary(hist));
        var bad = BatteryTrend.ParseReport("not xml");
        Assert.Null(bad.Now); Assert.Empty(bad.History);
    }

    [Fact]
    public void Battery_trend_prefers_the_csv_once_it_has_two_days()
    {
        var csv = BatteryTrend.ParseCsv("datum,gezondheid_pct,cycli\n2026-11-20,92.0,300\n2026-11-21,91.0,\nbroken\n");
        Assert.Equal(2, csv.Count);
        Assert.Null(csv[1].Cycles);
        var win = new List<BatteryTrend.Point> { new(Day.AddDays(-60), 97, 100) };
        Assert.Equal(csv, BatteryTrend.Merge(csv, win));
        Assert.Equal(2, BatteryTrend.Merge(csv.Take(1).ToList(), win).Count - 0 - 0);
        Assert.Equal("Since 2026-11-20: 92% → 91%", BatteryTrend.Summary(csv));   // cycles unknown at one end
        Assert.Equal("", BatteryTrend.Summary(csv.Take(1).ToList()));
    }

    [Fact]
    public void Reliability_history_to_a_daily_sparkline()
    {
        var json = "[{\"T\":\"2026-11-19T08:00:00.0000000Z\",\"I\":9.5},{\"T\":\"2026-11-19T20:00:00.0000000Z\",\"I\":8.0},{\"T\":\"2026-11-20T08:00:00.0000000Z\",\"I\":10},{\"T\":\"bad\",\"I\":5},{\"T\":\"2026-11-20T09:00:00Z\",\"I\":0}]";
        var p = Reliability.Parse(json);
        Assert.Equal(3, p.Count);
        Assert.Equal(2, Reliability.Daily(p).Count);
        Assert.Empty(Reliability.Parse("garbage"));
    }

    [Fact]
    public void Drain_window_starts_after_the_last_rise_and_shares_add_up()
    {
        var s = new[] { (Day, 50.0), (Day.AddMinutes(10), 80.0), (Day.AddMinutes(20), 79.0), (Day.AddMinutes(30), 70.0) };
        var w = Drain.Window(s, true, Day.AddHours(1))!.Value;
        Assert.Equal((Day.AddMinutes(10), 80.0, 70.0), (w.Start, w.PctStart, w.PctEnd));
        Assert.Equal(Day.Date, Drain.Window(s, false, Day.AddHours(1))!.Value.Start);
        Assert.Null(Drain.Window([(Day, 50.0)], true, Day));
        var rows = new List<AppsLog.Row>
        {
            new(Day.AddMinutes(15), "Zoom", "app", 30, 0, 0, 0, 0), new(Day.AddMinutes(16), "Zoom", "app", 30, 0, 0, 0, 0),
            new(Day.AddMinutes(15), "Mail", "app", 20, 0, 0, 0, 0), new(Day.AddMinutes(2), "Old", "app", 90, 0, 0, 0, 0),
        };
        var shares = Drain.Rank(rows, Day.AddMinutes(10), Day.AddHours(1));
        Assert.Equal(["Zoom", "Mail"], shares.Select(x => x.App));
        Assert.Equal(75, shares[0].Pct);
        Assert.Empty(Drain.Rank([], Day, Day.AddHours(1)));
    }

    static EventInfo Wake(string sleep, string wake, string src) =>
        new(Day, "Microsoft-Windows-Power-Troubleshooter", 1, [("SleepTime", sleep), ("WakeTime", wake), ("WakeSourceText", src)]);

    [Fact]
    public void Sleep_spans_lose_charge_or_were_on_the_charger()
    {
        var bat = new List<(DateTime, double)>();
        var s1 = Day.AddHours(-8).ToUniversalTime(); var w1 = Day.ToUniversalTime();
        bat.Add((Day.AddHours(-8).AddMinutes(-1), 80)); bat.Add((Day.AddMinutes(1), 74));
        var s2 = Day.AddDays(-2).AddHours(23).ToUniversalTime(); var w2 = Day.AddDays(-1).AddHours(7).ToUniversalTime();
        bat.Add((Day.AddDays(-2).AddHours(23), 40)); bat.Add((Day.AddDays(-1).AddHours(7), 90));
        var short1 = Day.AddDays(-3).ToUniversalTime();
        var spans = SleepSpans.Build([Wake(s1.ToString("o"), w1.ToString("o"), "Power Button"), Wake(s2.ToString("o"), w2.ToString("o"), "Device -USB Composite Device"),
            Wake(short1.ToString("o"), short1.AddMinutes(10).ToString("o"), "Lid")], bat);
        Assert.Equal(2, spans.Count);
        Assert.Equal((6.0, false, "power button"), (spans[0].Loss, spans[0].Charger, spans[0].Reason));
        Assert.True(spans[0].Heavy == (6 * 60 > spans[0].Minutes));
        Assert.Equal((null, true, "USB device"), (spans[1].Loss, spans[1].Charger, spans[1].Reason));
        Assert.Equal("lid opened", SleepSpans.WakeReason("Lid"));
        Assert.Equal("unknown", SleepSpans.WakeReason(""));
        Assert.Equal("network access (Wi-Fi/Bluetooth)", SleepSpans.WakeReason("Wake on Wi-Fi"));
    }

    [Fact]
    public void Awake_requests_in_plain_words()
    {
        var holds = AwakeRequests.From(CheckupParsers.ParsePowercfgRequests(
            "DISPLAY:\n[PROCESS] \\Device\\HarddiskVolume3\\Program Files\\Zoom\\Zoom.exe\nZoom is presenting.\n\nSYSTEM:\n[DRIVER] Realtek Audio\nAn audio stream is currently in use.\n[SERVICE] BackupSvc\nbackup\n\nAWAYMODE:\nNone.\n\nEXECUTION:\n[PROCESS] \\Device\\x\\Backup.exe\nrunning\n"));
        Assert.Equal([("Zoom", "keeping the screen on"), ("Audio playing", "preventing sleep"), ("BackupSvc", "preventing sleep"), ("Backup", "preventing sleep")],
            holds.Select(h => (h.App, h.Text)));
    }

    [Fact]
    public void Ssd_bench_result_roundtrips_and_classifies()
    {
        var r = new SsdBenchResult(3100, 2800, new DateTime(2026, 11, 18, 10, 42, 0, DateTimeKind.Local));
        var back = SsdBenchResult.Parse(r.ToJson())!;
        Assert.Equal((3100, 2800), (back.ReadMBps, back.WriteMBps));
        Assert.True(back.Fast); Assert.False(back.Slow);
        Assert.False(new SsdBenchResult(500, 450, DateTime.Now).Slow);      // a healthy SATA SSD
        Assert.True(new SsdBenchResult(500, 200, DateTime.Now).Slow);
        Assert.Null(SsdBenchResult.Parse("")); Assert.Null(SsdBenchResult.Parse("{\"read\":1}")); Assert.Null(SsdBenchResult.Parse("[1"));
        Assert.Equal("3.1 GB/s", CheckupFmt.Speed(3100)); Assert.Equal("850 MB/s", CheckupFmt.Speed(850));
        Assert.Equal(1073.7, Math.Round(SsdBenchResult.Speed(1), 1));
    }

    [Fact]
    public void Span_and_duration_text()
    {
        Assert.Equal("Sat 14:20–14:35", CheckupFmt.Span(Day.AddHours(14).AddMinutes(20), Day.AddHours(14).AddMinutes(35)));
        Assert.Equal("Sat 14:20", CheckupFmt.Span(Day.AddHours(14).AddMinutes(20), Day.AddHours(14).AddMinutes(21)));
        Assert.Equal("40 min", CheckupFmt.Mins(40)); Assert.Equal("2h 5m", CheckupFmt.Mins(125));
    }
}
