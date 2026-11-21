using System.Diagnostics;
using System.Text;
using Fusebox.Core.Checkup;
using Microsoft.Win32;

namespace Fusebox.Platform.Checkup;

/// <summary>Everything Checkup reads that is not the Event Log. All calls are blocking: run them on a background thread, once, when the page opens.</summary>
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

    // ---- stability index (cached 10 minutes) ----

    static double? stability; static DateTime stabilityAt = DateTime.MinValue;

    public static double? StabilityIndex()
    {
        if (DateTime.UtcNow - stabilityAt < TimeSpan.FromMinutes(10)) return stability;
        var json = PowerShell("Get-CimInstance Win32_ReliabilityStabilityMetrics | Sort-Object TimeGenerated -Descending | Select-Object -First 1 SystemStabilityIndex,TimeGenerated | ConvertTo-Json", 45000);
        stability = CheckupParsers.ParseStabilityIndex(json);
        stabilityAt = DateTime.UtcNow;
        return stability;
    }

    // ---- system facts ----

    public static bool PendingReboot()
    {
        using var a = Registry.LocalMachine.OpenSubKey(CurrentVersion + @"\WindowsUpdate\Auto Update\RebootRequired");
        using var b = Registry.LocalMachine.OpenSubKey(CurrentVersion + @"\Component Based Servicing\RebootPending");
        return a != null || b != null;
    }

    public static double UptimeDays() => Environment.TickCount64 / 86_400_000.0;

    /// <summary>Committed memory as % of the commit limit (RAM + page file).</summary>
    public static unsafe double CommitPct()
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)sizeof(Native.MEMORYSTATUSEX) };
        return Native.GlobalMemoryStatusEx(ref m) && m.ullTotalPageFile > 0 ? (m.ullTotalPageFile - m.ullAvailPageFile) * 100.0 / m.ullTotalPageFile : double.NaN;
    }

    public static List<DriveFree> Drives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new DriveFree(d.Name.TrimEnd('\\'), d.AvailableFreeSpace, d.TotalSize)).ToList();

    /// <summary>Works without admin.</summary>
    public static List<DiskInfo> Disks() =>
        CheckupParsers.ParseDisks(PowerShell("Get-PhysicalDisk | Select-Object FriendlyName,MediaType,HealthStatus,Size | ConvertTo-Json", 30000));

    // ---- battery (small duplicate of the Live battery reader on purpose: capacities only) ----

    public static BatteryHealth? Battery()
    {
        if (!CheckupNative.GetSystemPowerStatus(out var s) || s.BatteryFlag is 128 or 255) return null;   // 128 = no battery, 255 = unknown
        var json = PowerShell("$f=Get-CimInstance -Namespace root\\wmi BatteryFullChargedCapacity -EA 0|Select-Object -First 1;"
            + "$d=Get-CimInstance -Namespace root\\wmi BatteryStaticData -EA 0|Select-Object -First 1;"
            + "$c=Get-CimInstance -Namespace root\\wmi BatteryCycleCount -EA 0|Select-Object -First 1;"
            + "[pscustomobject]@{Full=$f.FullChargedCapacity;Design=$d.DesignedCapacity;Cycles=$c.CycleCount}|ConvertTo-Json", 30000);
        return CheckupParsers.ParseBattery(json) ?? new BatteryHealth(null, null);
    }

    // ---- startup apps ----

    // Task Scheduler "At log on" tasks are skipped: schtasks output is localised and ITaskService needs COM interop that is not worth it here.
    public static List<StartupItem> StartupItems()
    {
        var list = new List<StartupItem>();
        AddRun(list, Registry.CurrentUser, CurrentVersion + @"\Run", "Run", StartupSource.UserRun);
        AddRun(list, Registry.LocalMachine, CurrentVersion + @"\Run", "Run", StartupSource.MachineRun);
        AddRun(list, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", StartupSource.MachineRun32);
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, StartupSource.UserFolder);
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, StartupSource.CommonFolder);
        return list;
    }

    static string Approved(string leaf) => CurrentVersion + @"\Explorer\StartupApproved\" + leaf;

    static void AddRun(List<StartupItem> list, RegistryKey hive, string path, string approvedLeaf, StartupSource src)
    {
        using var key = hive.OpenSubKey(path);
        if (key == null) return;
        using var ap = hive.OpenSubKey(Approved(approvedLeaf));
        foreach (var name in key.GetValueNames())
            list.Add(new(name, key.GetValue(name)?.ToString() ?? "", src, CheckupParsers.StartupEnabled(ap?.GetValue(name) as byte[])));
    }

    static void AddFolder(List<StartupItem> list, string dir, RegistryKey hive, StartupSource src)
    {
        if (!Directory.Exists(dir)) return;
        using var ap = hive.OpenSubKey(Approved("StartupFolder"));
        foreach (var f in Directory.GetFiles(dir))
        {
            var file = Path.GetFileName(f);
            if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new(Path.GetFileNameWithoutExtension(f), f, src, CheckupParsers.StartupEnabled(ap?.GetValue(file) as byte[])));
        }
    }

    /// <summary>Same switch Task Manager flips. Only per-user items (no admin needed); returns false for the others.</summary>
    public static bool SetStartupEnabled(StartupItem item, bool enabled)
    {
        if (!item.CanToggle) return false;
        var (leaf, name) = item.Source == StartupSource.UserRun ? ("Run", item.Name) : ("StartupFolder", Path.GetFileName(item.Command));
        using var key = Registry.CurrentUser.CreateSubKey(Approved(leaf), true);
        key.SetValue(name, CheckupParsers.StartupApprovedValue(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
        return true;
    }
}
