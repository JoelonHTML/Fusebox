using Fusebox.Core.Look;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// The Settings window (FEATURES.md §9.1), shaped like Windows 11 Settings: resizable (default 1000×700, minimum 640×500 DIPs, size
/// remembered in settingsSize), sidebar with the Mac's tabs (Fans hidden on Windows, Battery only with a battery, "Menu bar" = "Tray icon"),
/// remembered tab (pref settingsTab), placed right of the main window each time it opens.
/// One instance; <see cref="Open"/> creates or brings it to the front. Each tab is its own Page in this folder.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>A sidebar tab: stored id (pref settingsTab, Mac ids), English title, Segoe Fluent glyph, page type.</summary>
    public sealed record Tab(string Id, string Title, string Glyph, Type Page);

    /// <summary>Sidebar order (§9.2). Battery is dropped on PCs without a battery.</summary>
    public static readonly Tab[] Tabs =
    [
        new("profile", "Profile", "\uE77B", typeof(ProfileSettingsPage)),
        new("look", "Appearance", "\uE771", typeof(AppearanceSettingsPage)),
        new("live", "Live page", "\uEC4A", typeof(LiveSettingsPage)),
        new("processes", "Processes", "\uE71D", typeof(ProcessesSettingsPage)),
        new("checkup", "Checkup", "\uE9D9", typeof(CheckupSettingsPage)),
        new("network", "Network", "\uE968", typeof(NetworkSettingsPage)),
        new("docked", "Docked", "\uE772", typeof(DockedSettingsPage)),
        new("menubar", "Tray icon", "\uE7C4", typeof(TraySettingsPage)),
        new("alerts", "Notifications", "\uEA8F", typeof(NotificationsSettingsPage)),
        new("battery", "Battery", "\uE857", typeof(BatterySettingsPage)),
        new("data", "Privacy", "\uEA18", typeof(PrivacySettingsPage)),
        new("about", "About", "\uE946", typeof(AboutSettingsPage)),
    ];

    public const double WidthDip = 1000, HeightDip = 700, MinWidthDip = 640, MinHeightDip = 500;
    static SettingsWindow? current;
    readonly List<Tab> shown;

    /// <summary>The open Settings window, or null.</summary>
    public static SettingsWindow? Current => current;

    /// <summary>Page type now shown (smoke test report).</summary>
    public string CurrentPage => Host.CurrentSourcePageType?.Name ?? "none";

    /// <summary>Opens Settings (or brings it to the front), optionally on a tab id (e.g. "about" from the "What's new" notification).</summary>
    public static void Open() => Open(null);

    public static void Open(string? tab)
    {
        if (tab != null) Prefs.Set("settingsTab", tab);
        if (current == null)
        {
            current = new SettingsWindow();
            current.Closed += (_, _) => current = null;
        }
        else if (tab != null) current.Select(tab);
        current.Place();
        current.AppWindow.Show();
        current.Activate();
    }

    SettingsWindow()
    {
        InitializeComponent();
        Title = L("Settings");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Fusebox.ico"));
        unsafe { SettingsNative.SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), &WndProc, 3, 0); }
        ThemeService.Attach(this, themed: false);
        shown = Tabs.Where(t => t.Id != "battery" || HasBattery()).ToList();
        foreach (var t in shown)
            Nav.MenuItems.Add(new NavigationViewItem { Content = L(t.Title), Tag = t.Id, Icon = new FontIcon { Glyph = t.Glyph } });
        Select(Prefs.Str("settingsTab"));
    }

    internal static bool HasBattery()
    {
        try { return Windows.System.Power.PowerManager.BatteryStatus != Windows.System.Power.BatteryStatus.NotPresent; }
        catch { return false; }
    }

    void Nav_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) =>
        sender.IsPaneToggleButtonVisible = args.DisplayMode != NavigationViewDisplayMode.Expanded;   // the menu button only when the sidebar is folded

    /// <summary>Switch the open window to a tab without moving it (e.g. "Change in Privacy").</summary>
    public static void GoTo(string tab) => current?.Select(tab);

    void Select(string id)
    {
        int i = Math.Max(shown.FindIndex(t => t.Id == id), 0);
        var item = Nav.MenuItems[i];
        if (!ReferenceEquals(Nav.SelectedItem, item)) Nav.SelectedItem = item;   // → Nav_SelectionChanged navigates
        else Show(shown[i]);
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string id }) return;
        var tab = shown.FirstOrDefault(t => t.Id == id);
        if (tab != null) Show(tab);
    }

    void Show(Tab t)
    {
        Prefs.Set("settingsTab", t.Id);
        if (Host.CurrentSourcePageType != t.Page)
            Host.Navigate(t.Page, null, ThemeService.Motion ? new EntranceNavigationTransitionInfo() : new SuppressNavigationTransitionInfo());
        Host.BackStack.Clear();
    }

    /// <summary>The remembered size in DIPs (settingsSize "w,h"), else 1000×700; never below the minimum.</summary>
    static (double W, double H) SavedSize()
    {
        var p = Prefs.Str("settingsSize").Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (p.Length == 2 && double.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out var w) && double.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var h))
            return (Math.Max(w, MinWidthDip), Math.Max(h, MinHeightDip));
        return (WidthDip, HeightDip);
    }

    /// <summary>§9.1 placement: right of the main window (gap 12), else left, else centered over it; tops aligned. Never larger than the screen.</summary>
    void Place()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized }) return;
        double s = Win32.Scale(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var (wd, hd) = SavedSize();
        var main = App.Main;
        var work = DisplayArea.GetFromWindowId(main != null && main.IsOnScreen ? main.AppWindow.Id : AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        // In DIPs, so the 12-DIP gap is the same at every scale.
        var screen = new Box(work.X / s, work.Y / s, work.Width / s, work.Height / s);
        wd = Math.Min(wd, screen.W);
        hd = Math.Min(hd, screen.H);
        int w = (int)(wd * s), h = (int)(hd * s);
        if (main == null || !main.IsOnScreen) { AppWindow.Resize(new(w, h)); return; }
        var mp = main.AppWindow.Position; var ms = main.AppWindow.Size;
        var (x, y) = Placement.Settings(new Box(mp.X / s, mp.Y / s, ms.Width / s, ms.Height / s), screen, wd, hd);
        AppWindow.MoveAndResize(new((int)(x * s), (int)(y * s), w, h));
    }

    /// <summary>End of a user resize: remember the size (not while maximized).</summary>
    void SaveSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
        double s = Win32.Scale(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Prefs.Set("settingsSize", FormattableString.Invariant($"{Math.Round(AppWindow.Size.Width / s)},{Math.Round(AppWindow.Size.Height / s)}"));
    }

    /// <summary>Minimum size (640×500 DIPs) and "resize ended" for the remembered size.</summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static unsafe nint WndProc(nint h, uint msg, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (msg == Win32.WM_GETMINMAXINFO)
            {
                var r = SettingsNative.DefSubclassProc(h, msg, wParam, lParam);
                double s = Win32.Scale(h);
                var mm = (Win32.MINMAXINFO*)lParam;
                mm->MinTrackSize.X = Math.Max(mm->MinTrackSize.X, (int)(MinWidthDip * s));
                mm->MinTrackSize.Y = Math.Max(mm->MinTrackSize.Y, (int)(MinHeightDip * s));
                return r;
            }
            if (msg == Win32.WM_EXITSIZEMOVE) current?.SaveSize();
        }
        catch { }
        return SettingsNative.DefSubclassProc(h, msg, wParam, lParam);
    }
}
