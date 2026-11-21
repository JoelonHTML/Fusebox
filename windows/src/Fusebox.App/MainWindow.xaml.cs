using Fusebox.App.Pages;
using Fusebox.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Fusebox.ico"));
        RestoreRect();
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>()) item.Content = Strings.L((string)item.Content);
        AppWindow.Closing += OnClosing;
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>Visible and not minimized. App.Live.Watched combines this with the tray panel.</summary>
    /// <summary>The page navigation (x:Name fields are private in WinUI).</summary>
    public NavigationView Navigation => Nav;

    /// <summary>Page type now in the frame (smoke test report).</summary>
    public string CurrentPage => ContentFrame.CurrentSourcePageType?.Name ?? "none";

    public bool IsOnScreen => AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>Close = hide to the system tray (setting "tray", default on) or quit.</summary>
    void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        SaveRect();
        if (App.Quitting) return;
        if (!Prefs.Get("tray", true) || App.Tray is not { Visible: true }) { App.Quit(); return; }
        e.Cancel = true;
        sender.Hide();
    }

    // Window size and position, "x,y,w,h" in Prefs "windowRect".
    void RestoreRect()
    {
        var size = new Windows.Graphics.SizeInt32(1100, 720);
        var p = Prefs.Get("windowRect", "").Split(',');
        if (p.Length == 4 && p.All(x => int.TryParse(x, out _)))
        {
            var r = new Windows.Graphics.RectInt32(int.Parse(p[0]), int.Parse(p[1]), Math.Max(int.Parse(p[2]), 480), Math.Max(int.Parse(p[3]), 360));
            // Only when the saved spot is still on a screen (monitor unplugged since?).
            if (DisplayArea.GetFromRect(r, DisplayAreaFallback.None) != null) { AppWindow.MoveAndResize(r); return; }
            size = new Windows.Graphics.SizeInt32(r.Width, r.Height);
        }
        AppWindow.Resize(size);
    }

    void SaveRect()
    {
        // Maximized/minimized windows are skipped: their size is not what we want to restore.
        if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
        var pos = AppWindow.Position; var size = AppWindow.Size;
        Prefs.Set("windowRect", $"{pos.X},{pos.Y},{size.Width},{size.Height}");
    }

    static Type PageFor(string? tag) => tag switch
    {
        "Processes" => typeof(ProcessesPage),
        "Checkup" => typeof(CheckupPage),
        "Network" => typeof(NetworkPage),
        "Docked" => typeof(DockedPage),
        "Settings" => typeof(SettingsPage),
        _ => typeof(LivePage),
    };

    /// <summary>
    /// Show a page by tag (Live, Processes, Checkup, Network, Docked, Settings) and keep the selection in sync.
    /// Navigates only when another page is showing, so a detail page under Live goes back to the cards.
    /// </summary>
    public void Open(string tag)
    {
        var page = PageFor(tag);
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
        object? item = tag == "Settings" ? Nav.SettingsItem : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item != null && !ReferenceEquals(Nav.SelectedItem, item)) Nav.SelectedItem = item;
    }

    static string TagOf(object? item, bool settings) => settings ? "Settings" : (item as NavigationViewItem)?.Tag as string ?? "Settings";

    // A click: IsSettingsInvoked is reliable here (programmatic selection of the settings item does not navigate, seen in CI).
    void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args) =>
        Open(TagOf(args.InvokedItemContainer, args.IsSettingsInvoked));

    // Keyboard and programmatic selection of the menu items.
    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        bool settings = args.IsSettingsSelected || (args.SelectedItem != null && ReferenceEquals(args.SelectedItem, sender.SettingsItem));
        if (args.SelectedItem == null && !settings) return;   // deselection
        Open(TagOf(args.SelectedItem, settings));
    }
}
