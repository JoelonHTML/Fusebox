using System.Diagnostics;
using Fusebox.Core;
using Fusebox.Core.Install;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;

namespace Fusebox.App;

/// <summary>
/// The "Installed apps" entry (HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Fusebox, no admin) and
/// <c>Fusebox.exe --uninstall [--quiet]</c>, which Windows runs from Settings › Apps › Installed apps › Uninstall.
/// </summary>
public static class Uninstaller
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Fusebox";
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>"0.2.3 beta" as shown in Windows (no build number).</summary>
    static string Version => BuildInfo.Version + (BuildInfo.Version.StartsWith("0.") ? " beta" : "");

    static long FolderBytes(string dir)
    {
        long n = 0;
        try { foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) n += f.Length; } catch { }
        return n;
    }

    /// <summary>Registers / refreshes the Windows entry, only when running from the install folder and something changed.</summary>
    public static void EnsureRegistered(string? exe = null, string? dir = null)
    {
        try
        {
            dir ??= AppContext.BaseDirectory.TrimEnd('\\', '/');
            exe ??= Environment.ProcessPath ?? Path.Combine(dir, "Fusebox.exe");
            if (BuildInfo.Build == 0 || !AppsList.IsInstalledCopy(dir, Local)) return;
            long kb = (FolderBytes(dir) + 1023) / 1024;
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            long? oldKb = k.GetValue("EstimatedSize") is int i ? i : null;
            if (!AppsList.NeedsWrite(k.GetValue("DisplayVersion") as string, oldKb, k.GetValue("DisplayIcon") as string, Version, kb, exe)) return;
            foreach (var (name, v) in AppsList.Values(exe, dir, Version, kb * 1024, DateTime.Now))
            {
                if (name == "InstallDate" && k.GetValue(name) != null) continue;   // keep the first install date
                k.SetValue(name, v, v is int ? RegistryValueKind.DWord : RegistryValueKind.String);
            }
        }
        catch { }
    }

    /// <summary>Program.Main for --uninstall; returns the exit code.</summary>
    public static int Run(string[] args)
    {
        try
        {
            Strings.Load(Prefs.Str("appLanguage"));
            bool quiet = args.Contains("--quiet");
            string dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            bool safe = AppsList.SafeToDelete(dir, Local, File.Exists(Path.Combine(dir, "Fusebox.exe")));
            if (!quiet && SettingsNative.MessageBox(0, Strings.L("Remove Fusebox from this PC? Your measurement history in Documents\\Fusebox is kept."), Strings.L("Uninstall Fusebox"), 0x24) != 6) return 1;
            bool settings = !quiet && SettingsNative.MessageBox(0, Strings.L("Also delete your settings?"), Strings.L("Uninstall Fusebox"), 0x24) == 6;

            StopOthers();
            try { Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run").DeleteValue("Fusebox", false); } catch { }
            try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Fusebox.lnk")); } catch { }
            try { AppNotificationManager.Default.UnregisterAll(); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (settings) { try { Directory.Delete(Path.Combine(Local, "Fusebox"), true); } catch { } }   // never Documents\Fusebox

            // The running exe can't delete itself: a hidden cmd waits 2 s and removes the folder.
            if (safe)
            {
                string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                Process.Start(new ProcessStartInfo(cmd, $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{dir}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory });
            }
            return 0;
        }
        catch { return 1; }
    }

    /// <summary>Closes other running Fusebox processes: ask politely, wait up to 10 s, then kill.</summary>
    static void StopOthers()
    {
        var others = Process.GetProcessesByName("Fusebox").Where(p => p.Id != Environment.ProcessId).ToList();
        foreach (var p in others) { try { p.CloseMainWindow(); } catch { } }
        for (int i = 0; i < 100 && others.Any(Alive); i++) Thread.Sleep(100);
        foreach (var p in others.Where(Alive)) { try { p.Kill(); p.WaitForExit(2000); } catch { } }
    }

    static bool Alive(Process p) { try { return !p.HasExited; } catch { return false; } }
}
