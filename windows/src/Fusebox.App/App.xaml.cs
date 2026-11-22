using Fusebox.App.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Fusebox.App;

public partial class App : Application
{
    static Task? gistLoad;
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
        // Tray flyout score row = today's Checkup gist (cached ≤ 10 min; loading it once is cheap: today's CSVs).
        TrayFlyout.GistProvider = () =>
        {
            if (gistLoad is not { IsCompleted: false }) gistLoad = Fusebox.App.Pages.CheckupData.GistAsync();   // one load at a time
            return Fusebox.App.Pages.CheckupData.GistNow() is { } g ? new TrayGist(g.Score, g.Reason, g.TipHead, g.TipRest, g.TipSerious) : null;
        };
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
        // Pass 1: default size, system (light) appearance. Pass 2: wide window, dark appearance, plus Settings › Appearance.
        var tabs = MainWindow.Tabs.Select(t => t.Tag).ToList();
        var items = tabs.Append("Settings").Append("wide-dark").Concat(tabs).Append("Settings:look").Append("tray").ToList();
        var report = new System.Text.StringBuilder();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastBeat = 0, worst = 0;
        var beat = Main!.DispatcherQueue.CreateTimer();
        beat.Interval = TimeSpan.FromMilliseconds(100);
        beat.Tick += (_, _) => { long now = clock.ElapsedMilliseconds; worst = Math.Max(worst, now - lastBeat - 100); lastBeat = now; };
        lastBeat = clock.ElapsedMilliseconds;
        beat.Start();
        int i = 0;
        string Shows(string tag) => tag.StartsWith("Settings") ? SettingsWindow.Current?.CurrentPage ?? "no Settings window" : tag is "wide-dark" or "tray" ? (Tray?.FlyoutOpen == true ? "flyout open" : "-") : Main.CurrentPage;
        void Go(string tag)
        {
            if (tag == "wide-dark")
            {
                SettingsWindow.Current?.Close();
                Prefs.Set("appearanceMode", "dark");
                Main.AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 680));
                Main.AppWindow.Move(new Windows.Graphics.PointInt32(10, 10));
                Main.Activate();
            }
            else if (tag.StartsWith("Settings:")) SettingsWindow.Open(tag[9..]);
            else if (tag == "tray") { SettingsWindow.Current?.Close(); Tray?.ToggleFlyout(); }
            else Main.Open(tag);
        }
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
                Go(items[i]);
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
