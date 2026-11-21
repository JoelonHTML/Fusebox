using Fusebox.Core;
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
        Strings.Load(Prefs.Get("language", "system"));
        Main = new MainWindow();
        Live.Watched = () => (Main?.IsOnScreen ?? false) || (Tray?.PanelOpen ?? false);
        Main.Activate();
        Live.Start();
        if (Prefs.Get("trayIcon", true)) Tray = new Tray();
        // A second Fusebox start redirects here (Program.Main): bring this window back.
        AppInstance.GetCurrent().Activated += (_, _) => Main.DispatcherQueue.TryEnqueue(ShowMain);
    }

    /// <summary>Bring the main window back (tray, second launch).</summary>
    public static void ShowMain()
    {
        if (Main == null) return;
        Main.AppWindow.Show();
        if (Main.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Main.Activate();
        Live.Refresh();
    }

    /// <summary>Really quit: flush the log, remove the tray icon, close everything.</summary>
    public static void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        Live.Shutdown();
        Tray?.Dispose();
        Current.Exit();
    }
}
