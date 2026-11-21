using System.Diagnostics;
using Fusebox.Core;
using Microsoft.UI.Windowing;

namespace Fusebox.App;

/// <summary>All startup work of Settings/Setup/Notifications/Hotkey/Low impact/Updater in one place.</summary>
public static class Boot
{
    /// <summary>Started with --background (start at login): window stays out of the way.</summary>
    public static bool Background { get; private set; }

    public static bool NeedsSetup => !Prefs.Bool("setupDone");

    /// <summary>Program.Main, before Application.Start (no UI yet; may start the moved copy and exit).</summary>
    public static void BeforeUi(string[] args)
    {
        Background = args.Contains("--background");
        int i = Array.IndexOf(args, "--wait-pid");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            // Started by the updater/installer: wait until the old process is gone.
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(120_000); } catch { }
        }
        if (Updater.RollBackIfNeverConfirmed()) Environment.Exit(0);
        Strings.Load(Prefs.Str("appLanguage"));
        Installer.CheckOnLaunch();
    }

    /// <summary>App.OnLaunched, after App.Main exists, activated and Live started.</summary>
    public static void OnLaunched()
    {
        var main = App.Main!;
        Notify.Register();
        Theming.Apply(main);
        LowImpact.Start();
        Hotkey.Start(main);
        Updater.Start();
        if (NeedsSetup) ShowSetup();
        else if (Background)
        {
            // Started at login: no window in your face. With the tray icon on it hides there, otherwise it is minimized.
            if ((Prefs.Str("menubarStyle") != "off")) main.AppWindow.Hide();
            else if (main.AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
        }
        // Still running after 3 s = this build starts: confirms an update (no rollback) and removes its leftovers.
        var t = App.Main!.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(3);
        t.IsRepeating = false;
        t.Tick += (_, _) => Updater.Confirm();
        t.Start();
    }

    static SetupWindow? setup;

    /// <summary>First launch, and "Run setup again" in Settings.</summary>
    public static void ShowSetup()
    {
        if (setup != null) { setup.Activate(); return; }
        setup = new SetupWindow();
        setup.Closed += (_, _) => setup = null;
        setup.Activate();
    }
}
