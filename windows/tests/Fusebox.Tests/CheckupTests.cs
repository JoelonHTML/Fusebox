using Fusebox.Core.Checkup;

namespace Fusebox.Tests;

public class CheckupTests
{
    const string Ns = "xmlns='http://schemas.microsoft.com/win/2004/08/events/event'";

    static string Xml(string provider, int id, string data, string time = "2026-11-20T02:30:00.0000000Z") =>
        $"<Event {Ns}><System><Provider Name='{provider}'/><EventID Qualifiers='0'>{id}</EventID><TimeCreated SystemTime='{time}'/></System><EventData>{data}</EventData></Event>";

    [Fact]
    public void Events_are_summarised()
    {
        var evs = new[]
        {
            Xml("Application Error", 1000, "<Data>chrome.exe</Data><Data>1.0</Data><Data>x</Data><Data>KERNELBASE.dll</Data>"),
            Xml("Application Error", 1000, "<Data>chrome.exe</Data>"),
            Xml("Application Hang", 1002, "<Data>Teams.exe</Data>"),
            Xml("Microsoft-Windows-WER-SystemErrorReporting", 1001, "<Data Name='param1'>0x0000009f</Data>"),
            Xml("Microsoft-Windows-Kernel-Power", 41, ""),
            "<nonsense",
        }.Select(CheckupParsers.ParseEvent).ToList();
        Assert.Null(evs[5]);
        Assert.Equal("KERNELBASE.dll", evs[0]!.At(3));
        var s = CheckupParsers.Summarize(evs.OfType<EventInfo>());
        Assert.Equal((2, 1, 1, 1), (s.AppCrashes, s.Hangs, s.BlueScreens, s.UnexpectedShutdowns));
        Assert.Equal(("chrome.exe", 2), s.TopApps[0]);
    }

    [Fact]
    public void Wakes_at_night_are_counted()
    {
        var wake = Xml("Microsoft-Windows-Power-Troubleshooter", 1,
            "<Data Name='SleepTime'>2026-11-19T22:00:00.000Z</Data><Data Name='WakeTime'>2026-11-20T02:30:00.000Z</Data><Data Name='WakeSourceText'>Power Button</Data>");
        var sleep = Xml("Microsoft-Windows-Kernel-Power", 42, "<Data Name='Reason'>7</Data>");
        var s = CheckupParsers.SummarizeSleep(new[] { wake, sleep }.Select(CheckupParsers.ParseEvent).OfType<EventInfo>());
        Assert.Equal(1, s.Sleeps);
        Assert.Single(s.Wakes);
        Assert.Equal("Power Button", s.Wakes[0].Source);
        Assert.Equal(s.Wakes[0].Time.Hour < 6 ? 1 : 0, s.NightWakes);
    }

    [Fact]
    public void Powercfg_requests_parse()
    {
        const string sample = "DISPLAY:\nNone.\n\nSYSTEM:\n[PROCESS] \\Device\\HarddiskVolume3\\Program Files\\Zoom\\Zoom.exe\nAn audio stream is in use.\n\n[DRIVER] Realtek Audio\nAn audio stream is currently in use.\n\nAWAYMODE:\nNone.\n\nEXECUTION:\n[PROCESS] \\Device\\x\\Backup.exe\nBackup running\n\nPERFBOOST:\nNone.\n\nACTIVELOCKSCREEN:\nNone.\n";
        var r = CheckupParsers.ParsePowercfgRequests(sample);
        Assert.Equal(3, r.Count);
        Assert.Equal("SYSTEM", r[0].Section);
        Assert.Contains("Zoom.exe", r[0].Who);
        Assert.Equal("An audio stream is in use.", r[0].Reason);
        Assert.Equal("EXECUTION", r[2].Section);
        Assert.Empty(CheckupParsers.ParsePowercfgRequests("DISPLAY:\nNone.\n"));
    }

    [Fact]
    public void Json_from_powershell_parses()
    {
        Assert.Equal(9.5, CheckupParsers.ParseStabilityIndex("{\"SystemStabilityIndex\":9.5,\"TimeGenerated\":\"x\"}"));
        Assert.Null(CheckupParsers.ParseStabilityIndex("garbage"));
        var d = CheckupParsers.ParseDisks("{\"FriendlyName\":\"Samsung\",\"MediaType\":4,\"HealthStatus\":0,\"Size\":512110190592}");
        Assert.Equal(new DiskInfo("Samsung", "SSD", "Healthy", 512110190592), d[0]);
        var (disks, rel, err) = CheckupParsers.ParseSsd("{\"disks\":[{\"FriendlyName\":\"A\",\"MediaType\":\"SSD\",\"HealthStatus\":\"Healthy\",\"Size\":1}],\"rel\":{\"DeviceId\":\"0\",\"Wear\":12,\"Temperature\":41,\"ReadErrorsTotal\":0,\"PowerOnHours\":800}}");
        Assert.Null(err);
        Assert.Single(disks);
        Assert.Equal(new DiskReliability("0", 12, 41, 0, 800), rel[0]);
        Assert.Equal("boom", CheckupParsers.ParseSsd("{\"error\":\"boom\"}").Error);
        Assert.Equal(new BatteryHealth(85, 312), CheckupParsers.ParseBattery("{\"Full\":42500,\"Design\":50000,\"Cycles\":312}"));
        Assert.Null(CheckupParsers.ParseBattery("{\"Full\":null,\"Design\":null,\"Cycles\":null}")!.Percent);
    }

    [Fact]
    public void Startup_approved_bytes()
    {
        Assert.True(CheckupParsers.StartupEnabled(null));
        Assert.True(CheckupParsers.StartupEnabled([0x02, 0, 0, 0]));
        Assert.True(CheckupParsers.StartupEnabled([0x06, 0, 0, 0]));
        Assert.False(CheckupParsers.StartupEnabled([0x03, 0, 0, 0]));
        Assert.False(CheckupParsers.StartupEnabled([0x07, 0, 0, 0]));
        var off = CheckupParsers.StartupApprovedValue(false, new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, off.Length);
        Assert.False(CheckupParsers.StartupEnabled(off));
        Assert.True(CheckupParsers.StartupEnabled(CheckupParsers.StartupApprovedValue(true, DateTime.UtcNow)));
    }

    [Fact]
    public void Healthy_pc_scores_high_and_a_sick_one_low()
    {
        var good = new CheckupInputs
        {
            CpuAvg = 8, CpuMax = 40, RamPct = 55, StartupEnabled = 5, UptimeDays = 2, PendingReboot = false,
            Drives = [new("C:", 300_000_000_000, 500_000_000_000)], Crashes = new(0, 0, 0, 0, []), StabilityIndex = 9.8,
        };
        var r = HealthScore.Evaluate(good);
        Assert.Equal(100, r.Score);
        Assert.Contains("Nothing wrong", HealthScore.WhySlow(good)[0]);

        var bad = new CheckupInputs
        {
            CpuAvg = 90, RamPct = 95, StartupEnabled = 25, UptimeDays = 40, PendingReboot = true,
            Drives = [new("C:", 20_000_000_000, 500_000_000_000)], Crashes = new(12, 2, 1, 1, [("chrome.exe", 8)]), StabilityIndex = 3,
            Battery = new(50, 900),
        };
        var rb = HealthScore.Evaluate(bad);
        Assert.True(rb.Score < 50, rb.Score.ToString());
        Assert.Equal(5, rb.Categories.Count);
        Assert.Contains(rb.Categories.SelectMany(c => c.Tips), t => t.Uri == "ms-settings:startupapps");
        Assert.True(HealthScore.WhySlow(bad).Count >= 5);
        Assert.Contains("Health score: ", HealthScore.Report(rb, DateTime.Now));
    }

    [Fact]
    public void No_battery_means_no_battery_category()
    {
        var r = HealthScore.Evaluate(new CheckupInputs { CpuAvg = 5 });
        Assert.DoesNotContain(r.Categories, c => c.Id == "battery");
    }
}
