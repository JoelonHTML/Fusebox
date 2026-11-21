using Fusebox.App.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Fusebox.App;

public partial class App : Application
{
    public static MainWindow? Main { get; private set; }
    public static readonly LiveHub Live = new();
    public static Tray? Tray { get; private set; }
    public static bool Quitting { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Main = new MainWindow();
        Live.Watched = () => (Main?.IsOnScreen ?? false) || (Tray?.FlyoutOpen ?? false);
        Main.Activate();
        Live.Start();
        // Tray icon unless its style is "off" (Mac menubarStyle). Prefs never allows it off together with the taskbar button.
        if (Prefs.Str("menubarStyle") != "off") Tray = new Tray();
        Prefs.Changed += k =>
        {
            if (k != "menubarStyle") return;
            Main.DispatcherQueue.TryEnqueue(() =>
            {
                bool on = Prefs.Str("menubarStyle") != "off";
                if (on && Tray == null) Tray = new Tray();
                else Tray?.SetVisible(on);
            });
        };
        Boot.OnLaunched();
        if (Environment.GetEnvironmentVariable("FUSEBOX_SMOKE") == "1") SmokeTour();
        // A second Fusebox start redirects here (Program.Main): bring this window back.
        AppInstance.GetCurrent().Activated += (_, _) => Main.DispatcherQueue.TryEnqueue(ShowMain);
    }

    /// <summary>
    /// CI only: Live 20 s, then every other tab 5 s, then the Settings window 5 s (the workflow screenshots each), then quit cleanly.
    /// Writes per page how long opening took and the longest UI-thread stall (a 100 ms heartbeat that arrived late).
    /// </summary>
    static void SmokeTour()
    {
        var items = MainWindow.Tabs.Select(t => t.Tag).Append("Settings").ToList();
        var report = new System.Text.StringBuilder();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastBeat = 0, worst = 0;
        var beat = Main!.DispatcherQueue.CreateTimer();
        beat.Interval = TimeSpan.FromMilliseconds(100);
        beat.Tick += (_, _) => { long now = clock.ElapsedMilliseconds; worst = Math.Max(worst, now - lastBeat - 100); lastBeat = now; };
        lastBeat = clock.ElapsedMilliseconds;
        beat.Start();
        int i = 0;
        string Shows(string tag) => tag == "Settings" ? SettingsWindow.Current?.CurrentPage ?? "no Settings window" : Main.CurrentPage;
        var t = Main.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(20);   // first 20 s on Live: the workflow measures idle CPU there
        t.Tick += (_, _) =>
        {
            if (t.Interval != TimeSpan.FromSeconds(5)) { t.Stop(); t.Interval = TimeSpan.FromSeconds(5); t.Start(); }   // a new interval only applies after a restart
            report.AppendLine($"{items[i]}: longest UI stall {worst} ms, shows {Shows(items[i])}");
            worst = 0;
            if (++i < items.Count)
            {
                long t0 = clock.ElapsedMilliseconds;
                Main.Open(items[i]);
                report.Append($"{items[i]}: opened at {t0 / 1000.0:0.0} s, open {clock.ElapsedMilliseconds - t0} ms; ");
                lastBeat = clock.ElapsedMilliseconds;
                return;
            }
            t.Stop(); beat.Stop();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-ok.txt"), report.ToString());
            Quit();
        };
        t.Start();
    }

    /// <summary>Bring the main window back (tray, hotkey, second launch).</summary>
    public static void ShowMain()
    {
        if (Main == null) return;
        Main.AppWindow.Show();
        if (Main.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Main.Activate();
        Live.Refresh();
    }

    /// <summary>Main window on a tab (tray flyout tiles, notifications).</summary>
    public static void ShowMain(string tag, object? parameter = null)
    {
        ShowMain();
        Main?.Open(tag, parameter);
    }

    /// <summary>Really quit: flush the logs, remove the tray icon, close everything.</summary>
    public static void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        Live.Shutdown();
        Tray?.Dispose();
        Current.Exit();
    }
}
