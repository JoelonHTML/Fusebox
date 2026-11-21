using Fusebox.Core.Checkup;

namespace Fusebox.Tests;

public class CheckupTests
{
    const string Ns = "xmlns='http://schemas.microsoft.com/win/2004/08/events/event'";

    static string Xml(string provider, int id, string data, string time = "2026-11-20T02:30:00.0000000Z") =>
        $"<Event {Ns}><System><Provider Name='{provider}'/><EventID Qualifiers='0'>{id}</EventID><TimeCreated SystemTime='{time}'/></System><EventData>{data}</EventData></Event>";

    [Fact]
    public void Events_parse()
    {
        var e = CheckupParsers.ParseEvent(Xml("Application Error", 1000, "<Data>chrome.exe</Data><Data>1.0</Data><Data>x</Data><Data>KERNELBASE.dll</Data>"))!;
        Assert.Equal("KERNELBASE.dll", e.At(3));
        Assert.Equal(1000, e.Id);
        Assert.Null(CheckupParsers.ParseEvent("<nonsense"));
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

}
