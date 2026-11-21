using System.Diagnostics;
using Fusebox.Core;
using Microsoft.UI.Windowing;

namespace Fusebox.App;

/// <summary>All startup work of Settings/Setup/Notifications/Hotkey/Low impact/Updater in one place.</summary>
public static class Boot
{
    /// <summary>Started with --background (start at login): window stays out of the way.</summary>
    public static bool Background { get; private set; }

    public static bool NeedsSetup => !Prefs.Get("setupDone", false);

    /// <summary>Program.Main, before Application.Start (no UI yet; may start the moved copy and exit).</summary>
    public static void BeforeUi(string[] args)
    {
        Background = args.Contains("--background");
        int i = Array.IndexOf(args, "--wait-pid");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            // Started by the updater/installer: wait until the old process is gone.
            try { Process.GetProcessById(pid).WaitForExit(10_000); } catch { }
        }
        Strings.Load(Prefs.Get("language", "system"));
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
            if (Prefs.Get("tray", true)) main.AppWindow.Hide();
            else if (main.AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
        }
        // Leftovers of an update (*.old) can only go once the old process has exited.
        var t = App.Main!.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(30);
        t.IsRepeating = false;
        t.Tick += (_, _) => Updater.CleanUp();
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
