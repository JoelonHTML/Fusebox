using Fusebox.Core;
using Microsoft.UI.Xaml;

namespace Fusebox.App;

public partial class App : Application
{
    public static MainWindow? Main { get; private set; }
    public static readonly LiveHub Live = new();

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Strings.Load(Prefs.Get("language", "system"));
        Main = new MainWindow();
        Main.Activate();
        Live.Start();
    }
}
