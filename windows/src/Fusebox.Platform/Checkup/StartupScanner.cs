using System.Diagnostics;
using Fusebox.Core.Checkup;
using Microsoft.Win32;

namespace Fusebox.Platform.Checkup;

/// <summary>
/// Everything that starts with Windows (§5.B.1 on Windows): Run / RunOnce (HKCU, HKLM, WOW6432Node), the Startup folders, Task Scheduler logon
/// tasks (schtasks /query /xml, parsed in Core) and auto-start services from other makers. State comes from Explorer\StartupApproved like
/// Task Manager. Blocking: run on a background thread, once per page visit.
/// </summary>
public static class StartupScanner
{
    const string Cv = @"Software\Microsoft\Windows\CurrentVersion";
    static string Approved(string leaf) => Cv + @"\Explorer\StartupApproved\" + leaf;

    public static List<StartupItem> Scan()
    {
        var list = new List<StartupItem>();
        Guard(() => AddRun(list, Registry.CurrentUser, Cv + @"\Run", "Run", StartupKind.Run, StartupScope.User));
        Guard(() => AddRun(list, Registry.CurrentUser, Cv + @"\RunOnce", null, StartupKind.RunOnce, StartupScope.User));
        Guard(() => AddRun(list, Registry.LocalMachine, Cv + @"\Run", "Run", StartupKind.Run, StartupScope.Machine));
        Guard(() => AddRun(list, Registry.LocalMachine, Cv + @"\RunOnce", null, StartupKind.RunOnce, StartupScope.Machine));
        Guard(() => AddRun(list, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", StartupKind.Run, StartupScope.Machine32));
        Guard(() => AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, StartupScope.User));
        Guard(() => AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, StartupScope.Machine));
        Guard(() => AddTasks(list));
        Guard(() => AddServices(list));
        Resolve(list);
        return list;
    }

    static void Guard(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    static void AddRun(List<StartupItem> list, RegistryKey hive, string path, string? approvedLeaf, StartupKind kind, StartupScope scope)
    {
        using var key = hive.OpenSubKey(path);
        if (key == null) return;
        using var ap = approvedLeaf == null ? null : hive.OpenSubKey(Approved(approvedLeaf));
        foreach (var name in key.GetValueNames())
        {
            var cmd = key.GetValue(name)?.ToString() ?? "";
            list.Add(new() { Id = name, Raw = name, Name = name, Command = cmd, Kind = kind, Scope = scope, Enabled = CheckupParsers.StartupEnabled(ap?.GetValue(name) as byte[]) });
        }
    }

    static void AddFolder(List<StartupItem> list, string dir, RegistryKey hive, StartupScope scope)
    {
        if (!Directory.Exists(dir)) return;
        using var ap = hive.OpenSubKey(Approved("StartupFolder"));
        foreach (var f in Directory.GetFiles(dir))
        {
            var file = Path.GetFileName(f);
            if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new() { Id = file, Raw = Path.GetFileNameWithoutExtension(f), Name = Path.GetFileNameWithoutExtension(f), Command = f, Kind = StartupKind.Folder, Scope = scope,
                Enabled = CheckupParsers.StartupEnabled(ap?.GetValue(file) as byte[]), Exe = f });
        }
    }

    static void AddTasks(List<StartupItem> list)
    {
        // The console code page is not UTF-8, so a non-ASCII task name can come out garbled; harmless (only used as a label / switch id of that task).
        var xml = CheckupReader.Run(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/query /xml ONE", 30000);
        foreach (var t in StartupParse.ParseTasks(xml))
            list.Add(new() { Id = t.Path, Raw = t.Name, Name = t.Name, Command = t.Command, Kind = StartupKind.Task, Scope = StartupScope.Machine, Enabled = t.Enabled });
    }

    // ---- services: auto-start, own or shared process, not from Microsoft ----

    static void AddServices(List<StartupItem> list)
    {
        var remembered = DisabledServices.Read();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root == null) return;
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k == null || k.GetValue("Type") is not int type || (type & 0x30) == 0) continue;   // 0x10 own process, 0x20 shared; drivers are not services
            int start = k.GetValue("Start") is int s ? s : 3;
            bool wasOff = start >= 3 && remembered.Contains(name);
            if (start != 2 && !wasOff) continue;
            if (k.GetValue("ImagePath") is not string image) continue;
            var exe = StartupParse.ExePath(image);
            if (exe == null || exe.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) continue;   // Windows' own
            var display = k.GetValue("DisplayName") as string;
            list.Add(new() { Id = name, Raw = name, Name = display != null && !display.StartsWith('@') ? display : name, Command = image, Kind = StartupKind.Service,
                Scope = StartupScope.Machine, Enabled = start == 2, Exe = exe });
        }
    }

    // ---- orphans, friendly names, running state and usage ----

    sealed record Proc(string Path, double Cpu, double MemMb);

    static void Resolve(List<StartupItem> list)
    {
        var procs = RunningByPath();
        foreach (var i in list)
        {
            i.Exe ??= StartupParse.ExePath(i.Command);
            i.Orphan = i.Kind != StartupKind.Folder && StartupParse.IsOrphan(i.Exe, p => File.Exists(p) || Directory.Exists(p));
            string? product = null;
            if (!i.Orphan && i.Exe != null && i.Kind != StartupKind.Folder && File.Exists(i.Exe))
            {
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(i.Exe);
                    if (i.Kind == StartupKind.Service) { if (v.CompanyName?.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true) i.Name = ""; }
                    else product = v.ProductName;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (i.Name.Length == 0) continue;
            if (i.Kind != StartupKind.Service) i.Name = StartupParse.Friendly(i.Raw, i.Exe, product);
            var key = i.Exe != null && i.Kind != StartupKind.Folder ? i.Exe.ToLowerInvariant() : null;
            if (key != null && procs.TryGetValue(key, out var ps))
            {
                i.Running = true;
                i.Cpu = ps.Sum(x => x.Cpu);
                i.MemMb = ps.Sum(x => x.MemMb);
            }
        }
        list.RemoveAll(i => i.Name.Length == 0);   // Microsoft services that slipped through
    }

    /// <summary>Running processes by exe path (lower case): % of the whole PC averaged since the process started, and working set. Processes we may not open are skipped.</summary>
    static Dictionary<string, List<Proc>> RunningByPath()
    {
        var map = new Dictionary<string, List<Proc>>();
        int cores = Environment.ProcessorCount;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path == null) continue;
                    double life = Math.Max((DateTime.Now - p.StartTime).TotalSeconds, 1);
                    var cpu = p.TotalProcessorTime.TotalSeconds / life / cores * 100;
                    (map.TryGetValue(path.ToLowerInvariant(), out var l) ? l : map[path.ToLowerInvariant()] = []).Add(new(path, cpu, p.WorkingSet64 / 1048576.0));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return map;
    }

    // ---- switching ----

    /// <summary>
    /// Per-user items: the same switch Task Manager flips (StartupApproved in HKCU), no admin needed. False for everything else:
    /// use <see cref="SetElevatedAsync"/>.
    /// </summary>
    public static bool SetDirect(StartupItem item, bool enabled)
    {
        if (!item.CanToggleDirect) return false;
        var (leaf, name) = item.Kind == StartupKind.Run ? ("Run", item.Id) : ("StartupFolder", item.Id);
        using var key = Registry.CurrentUser.CreateSubKey(Approved(leaf), true);
        key.SetValue(name, CheckupParsers.StartupApprovedValue(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
        return true;
    }

    /// <summary>HKLM Run, common Startup folder, tasks and services through the elevated PowerShell task (UAC). True when Windows did it.</summary>
    public static async Task<bool> SetElevatedAsync(StartupItem item, bool enabled)
    {
        if (ElevatedScripts.Toggle(item.Kind, item.Scope, item.Id, enabled) is not { } body) return false;
        var result = await Elevated.RunBodyAsync(body);
        bool ok = result != null && result.StartsWith("ok", StringComparison.Ordinal);
        if (ok && item.Kind == StartupKind.Service) DisabledServices.Set(item.Id, !enabled);
        return ok;
    }
}

/// <summary>Services Fusebox switched off, so they stay in the list (a manual-start service is not auto-start any more) and can be switched back on.</summary>
static class DisabledServices
{
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fusebox", "disabled-services.txt");

    public static HashSet<string> Read()
    {
        try { return File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) : []; }
        catch (IOException) { return []; }
    }

    public static void Set(string service, bool off)
    {
        try
        {
            var all = Read();
            if (off) all.Add(service); else all.Remove(service);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, all);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
