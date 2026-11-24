using System.Diagnostics;
using System.Text;
using Fusebox.Core.Checkup;
using Microsoft.Win32;

namespace Fusebox.Platform.Checkup;

/// <summary>Everything Checkup reads that is not the Event Log, the startup scan or the disk scan. All calls block: run them on a background thread, on demand.</summary>
public static class CheckupReader
{
    const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion";

    // ---- processes ----

    public static string Run(string file, string args, int timeoutMs = 30000, Encoding? enc = null)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, CreateNoWindow = true, UseShellExecute = false, StandardOutputEncoding = enc ?? Encoding.UTF8 };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(file);
        var read = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return ""; }
        return read.GetAwaiter().GetResult();
    }

    public static string PowerShell(string script, int timeoutMs = 30000)
    {
        script = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return Run(Elevated.PowerShellExe, "-NoProfile -NonInteractive -EncodedCommand " + b64, timeoutMs);
    }

    static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    // ---- Windows' own history (day one) ----

    static List<Reliability.Point>? reliability; static DateTime reliabilityAt = DateTime.MinValue;

    /// <summary>Reliability Monitor's stability index for the last 30 days (cached 10 minutes). Empty when Windows has none.</summary>
    static readonly object reliabilityGate = new();

    public static List<Reliability.Point> ReliabilityHistory()
    {
        lock (reliabilityGate)   // two quick card rebuilds would otherwise both start a 45 s PowerShell
        {
        if (reliability != null && DateTime.UtcNow - reliabilityAt < TimeSpan.FromMinutes(10)) return reliability;
        var json = PowerShell("Get-CimInstance Win32_ReliabilityStabilityMetrics | Where-Object { $_.TimeGenerated -gt (Get-Date).AddDays(-30) } | "
            + "Select-Object @{n='T';e={$_.TimeGenerated.ToUniversalTime().ToString('o')}},@{n='I';e={$_.SystemStabilityIndex}} | ConvertTo-Json -Compress", 45000);
        reliabilityAt = DateTime.UtcNow;
        return reliability = Reliability.Parse(json);
        }
    }

    /// <summary><c>powercfg /batteryreport /xml</c> (no admin): present capacity, cycles and the capacity history. Null without a battery or on failure.</summary>
    public static (BatteryHealth? Now, List<BatteryTrend.Point> History)? BatteryReport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fusebox-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "battery.xml");
            Run(System32("powercfg.exe"), $"/batteryreport /xml /output \"{file}\"", 30000);
            if (!File.Exists(file)) return null;
            var r = BatteryTrend.ParseReport(File.ReadAllText(file));
            return r.Now == null && r.History.Count == 0 ? null : r;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- system facts ----

    public static bool PendingReboot()
    {
        using var a = Registry.LocalMachine.OpenSubKey(CurrentVersion + @"\WindowsUpdate\Auto Update\RebootRequired");
        using var b = Registry.LocalMachine.OpenSubKey(CurrentVersion + @"\Component Based Servicing\RebootPending");
        return a != null || b != null;
    }

    /// <summary>When Windows Update last installed something successfully (registry, local time), null when unknown.</summary>
    public static DateTime? LastUpdate()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(CurrentVersion + @"\WindowsUpdate\Auto Update\Results\Install");
            return k?.GetValue("LastSuccessTime") is string s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t) ? t.ToLocalTime() : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    public static double UptimeDays() => Environment.TickCount64 / 86_400_000.0;
    public static DateTime Boot() => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static List<DriveFree> Drives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveFree(d.Name.TrimEnd('\\'), d.AvailableFreeSpace, d.TotalSize)).ToList();

    /// <summary>"11 Pro 24H2 (build 26100)".</summary>
    public static string WindowsVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var name = k?.GetValue("ProductName") as string ?? "Windows";
            var build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            if (int.TryParse(build, out var b) && b >= 22000) name = name.Replace("Windows 10", "Windows 11");
            var disp = k?.GetValue("DisplayVersion") as string;
            return name.Replace("Windows ", "") + (disp != null ? " " + disp : "") + (build.Length > 0 ? $" (build {build})" : "");
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>For the report scrubber: user name, display name, computer name, domain.</summary>
    public static ScrubIdentity Identity()
    {
        string full = "";
        try { full = CheckupNative.DisplayName(); } catch { }
        return new(Environment.UserName, full, Environment.MachineName, Environment.UserDomainName);
    }

    /// <summary>Works without admin.</summary>
    public static List<DiskInfo> Disks() =>
        CheckupParsers.ParseDisks(PowerShell("Get-PhysicalDisk | Select-Object FriendlyName,MediaType,HealthStatus,Size | ConvertTo-Json", 30000));
}
