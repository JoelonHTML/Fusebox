using System.Diagnostics;
using System.Text.RegularExpressions;
using Fusebox.Core;

namespace Fusebox.App;

/// <summary>
/// Counterpart of the Mac Installer (§11.8): started from somewhere else (Downloads), Fusebox offers once to move itself to
/// %LOCALAPPDATA%\Programs\Fusebox, and when a copy is already there it compares builds: the same → open the installed one,
/// a newer one installed → offer to open that, an older one → offer to replace it (settings and history stay: they live in LocalAppData).
/// Runs before any window exists, so the questions are plain message boxes.
/// </summary>
public static partial class Installer
{
    const uint YesNo = 0x24, Error = 0x10;   // MB_YESNO | MB_ICONQUESTION, MB_ICONERROR
    const int Yes = 6;

    /// <summary>Call before any window exists. Returns normally unless it started the other copy and exited.</summary>
    public static void CheckOnLaunch()
    {
        try
        {
            if (BuildInfo.Build == 0 || Environment.GetEnvironmentVariable("FUSEBOX_NO_INSTALL") != null) return;
            string me = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            string target = Path.Combine(programs, "Fusebox");
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (me.StartsWith(programs, StringComparison.OrdinalIgnoreCase) || me.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return;
            string installed = Path.Combine(target, "Fusebox.exe");
            if (File.Exists(installed)) Another(me, target, installed);
            else MoveHere(me, target);
        }
        catch { }
    }

    [GeneratedRegex(@"\+(\d{8,})")]
    private static partial Regex BuildInVersion();

    static (string Version, long Build) Describe(string exe)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "";
            var m = BuildInVersion().Match(v);
            string ver = v.Split('+')[0];
            return ((ver.StartsWith("0.") ? ver + " beta" : ver) + (m.Success ? $" ({m.Groups[1].Value})" : ""), m.Success ? long.Parse(m.Groups[1].Value) : 0);
        }
        catch { return ("?", 0); }
    }

    static void Ask(string text, string title, Action yes, uint icon = YesNo)
    {
        if (SettingsNative.MessageBox(0, text, title, icon) == Yes) yes();
    }

    /// <summary>Another copy is installed: same build → just open it; newer → offer to open it; older → offer to replace it.</summary>
    static void Another(string me, string target, string installed)
    {
        var (theirsText, theirs) = Describe(installed);
        long mine = BuildInfo.Build;
        string mineText = Updater.CurrentVersion;
        if (theirs == mine) { OpenAndExit(target); return; }
        if (theirs > mine)
        {
            if (Prefs.Bool("installAsked")) return;
            Prefs.Set("installAsked", true);
            Ask(Strings.L("This copy (%@) is older than the one in Programs (%@).", mineText, theirsText) + "\n\n" + Strings.L("Open installed version"),
                Strings.L("A newer Fusebox is already installed"), () => OpenAndExit(target));
            return;
        }
        Ask(Strings.L("Replace Fusebox %@ in Programs with this version (%@). Your settings and history stay.", theirsText, mineText),
            Strings.L("Update Fusebox?"), () =>
            {
                try
                {
                    if (!StopRunning(installed)) { SettingsNative.MessageBox(0, Strings.L("The old Fusebox is still running. Quit it from its tray icon and open this one again."), "Fusebox", Error); return; }
                    Place(me, target);
                }
                catch (Exception e) { SettingsNative.MessageBox(0, Strings.L("Couldn't update Fusebox: %@. Copy Fusebox into Programs yourself.", e.Message), "Fusebox", Error); }
            });
    }

    /// <summary>Not installed: offer once to move here.</summary>
    static void MoveHere(string me, string target)
    {
        if (Prefs.Bool("installAsked")) return;
        Prefs.Set("installAsked", true);
        Ask(Strings.L("Then it's easy to find, and future updates replace it automatically."), Strings.L("Move Fusebox to Programs?"), () =>
        {
            try { Place(me, target); }
            catch (Exception e) { SettingsNative.MessageBox(0, Strings.L("Couldn't move Fusebox: %@", e.Message), "Fusebox", Error); }
        });
    }

    /// <summary>Copies this copy into place, makes the Start menu shortcut, starts it and exits.</summary>
    static void Place(string me, string target)
    {
        CopyDir(me, target);
        var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Fusebox.lnk");
        SettingsNative.CreateShortcut(lnk, Path.Combine(target, "Fusebox.exe"), target);
        OpenAndExit(target);
    }

    static void OpenAndExit(string target)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(target, "Fusebox.exe"), $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = target });
        Environment.Exit(0);
    }

    /// <summary>Closes the installed copy if it runs; waits up to ~5 s. False when it is still there.</summary>
    static bool StopRunning(string exe)
    {
        var running = Process.GetProcessesByName("Fusebox").Where(p => { try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return false; } }).ToList();
        foreach (var p in running) { try { p.CloseMainWindow(); p.Kill(); } catch { } }
        for (int i = 0; i < 50 && running.Any(p => { try { return !p.HasExited; } catch { return false; } }); i++) Thread.Sleep(100);
        return !running.Any(p => { try { return !p.HasExited; } catch { return false; } });
    }

    static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }
}
