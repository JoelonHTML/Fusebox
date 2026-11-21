using System.Diagnostics;
using Fusebox.Core;

namespace Fusebox.App;

/// <summary>Counterpart of the Mac Installer: offer once to move Fusebox from e.g. Downloads to %LOCALAPPDATA%\Programs\Fusebox.</summary>
public static class Installer
{
    /// <summary>Call before any window exists. Returns normally unless it started the moved copy and exited.</summary>
    public static void CheckOnLaunch()
    {
        try
        {
            if (BuildInfo.Build == 0 || Environment.GetEnvironmentVariable("FUSEBOX_NO_INSTALL") != null || Prefs.Get("installAsked", false)) return;
            string me = AppContext.BaseDirectory.TrimEnd('\\', '/');
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            string target = Path.Combine(programs, "Fusebox");
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (me.StartsWith(programs, StringComparison.OrdinalIgnoreCase) || me.StartsWith(pf, StringComparison.OrdinalIgnoreCase)) return;
            Prefs.Set("installAsked", true);
            // MB_YESNO | MB_ICONQUESTION
            if (SettingsNative.MessageBox(0, Strings.L("Then it's easy to find, and future updates replace it automatically."),
                    Strings.L("Move Fusebox to Programs?"), 0x24) != 6) return;
            try
            {
                CopyDir(me, target);
                var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Fusebox.lnk");
                SettingsNative.CreateShortcut(lnk, Path.Combine(target, "Fusebox.exe"), target);
                Process.Start(new ProcessStartInfo(Path.Combine(target, "Fusebox.exe"), $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = target });
                Environment.Exit(0);
            }
            catch (Exception e)
            {
                SettingsNative.MessageBox(0, Strings.L("Couldn't move Fusebox: %@", e.Message), "Fusebox", 0x10);
            }
        }
        catch { }
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
