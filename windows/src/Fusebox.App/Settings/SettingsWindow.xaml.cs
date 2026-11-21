using Fusebox.Core.Look;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// The Settings window (FEATURES.md §9.1): fixed 740×560, not resizable, sidebar with the Mac's tabs (Fans hidden on Windows, Battery
/// only with a battery, "Menu bar" = "Tray icon"), remembered tab (pref settingsTab), placed right of the main window each time it opens.
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

    public const double WidthDip = 740, HeightDip = 560;
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
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsResizable = false; p.IsMaximizable = false; }
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

    /// <summary>§9.1 placement: right of the main window (gap 12), else left, else centered over it; tops aligned.</summary>
    void Place()
    {
        double s = Win32.Scale(WinRT.Interop.WindowNative.GetWindowHandle(this));
        int w = (int)(WidthDip * s), h = (int)(HeightDip * s);
        var main = App.Main;
        if (main == null || !main.IsOnScreen) { AppWindow.Resize(new(w, h)); return; }
        var mp = main.AppWindow.Position; var ms = main.AppWindow.Size;
        var work = DisplayArea.GetFromWindowId(main.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        // In DIPs, so the 12-DIP gap is the same at every scale.
        var (x, y) = Placement.Settings(new Box(mp.X / s, mp.Y / s, ms.Width / s, ms.Height / s),
                                        new Box(work.X / s, work.Y / s, work.Width / s, work.Height / s), WidthDip, HeightDip);
        AppWindow.MoveAndResize(new((int)(x * s), (int)(y * s), w, h));
    }
}
