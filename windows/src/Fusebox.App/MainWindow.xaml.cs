using Fusebox.App.Pages;
using Fusebox.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App;

public sealed partial class MainWindow : Window
{
    bool visible = true;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Fusebox.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>()) item.Content = Strings.L((string)item.Content);
        VisibilityChanged += (_, e) => visible = e.Visible;
        App.Live.Watched = () => IsOnScreen;
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    /// <summary>Visible and not minimized. The tray panel adds itself to LiveHub.Watched separately.</summary>
    public bool IsOnScreen => visible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected ? typeof(SettingsPage) : ((args.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "Processes" => typeof(ProcessesPage),
            "Checkup" => typeof(CheckupPage),
            "Network" => typeof(NetworkPage),
            "Docked" => typeof(DockedPage),
            _ => typeof(LivePage),
        };
        ContentFrame.Navigate(page);
    }
}
