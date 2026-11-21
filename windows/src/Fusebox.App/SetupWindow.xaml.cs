using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App;

/// <summary>First-run setup, one question per step (Mac "Setup"). Every choice applies immediately; "Done" sets Prefs "setupDone".</summary>
public sealed partial class SetupWindow : Window
{
    int step;
    const int Last = 6;

    public SetupWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 620));
        Title = Strings.L("Setup");
        ThemeService.Attach(this, themed: false);
        Show();
    }

    void Back_Click(object sender, RoutedEventArgs e) { step--; Show(); }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (step < Last) { step++; Show(); return; }
        Prefs.Set("setupDone", true);
        Close();
    }

    void Show()
    {
        Host.Children.Clear();
        BackButton.Content = Strings.L("Back");
        BackButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = step == Last ? Strings.L("Done") : Strings.L("Next");
        (string title, string body) = step switch
        {
            0 => ("Welcome to Fusebox", "Fusebox shows how busy your PC is: processor, memory, storage, internet and battery."),
            1 => ("Pick a style", "This is how the Fusebox window looks. You can switch any time."),
            2 => ("What do you want to see?", "Turn measurements on or off."),
            3 => ("Show Fusebox in the system tray?", "A small icon next to the clock, so you can check at a glance."),
            4 => ("How should Fusebox start?", "Fusebox uses very little power, so it's fine to keep it running."),
            5 => ("Should Fusebox warn you?", "A notification when something needs attention, like your PC getting too hot."),
            _ => ("You're all set!", "You can change all of this later in Settings."),
        };
        TitleText.Text = Strings.L(title);
        BodyText.Text = Strings.L(body);
        switch (step)
        {
            case 1:
                var current = ThemeService.Current.Id;
                foreach (var s in Fusebox.Core.Look.Themes.All)
                {
                    var id = s.Id;
                    var r = new RadioButton { Content = s.Name, GroupName = "style", IsChecked = id == current };   // style names are never translated
                    r.Checked += (_, _) => Prefs.Set("theme", id);
                    Host.Children.Add(r);
                }
                break;
            case 2: Host.Children.Add(MetricsEditor.Create()); break;
            case 3:
                var tray = new ToggleSwitch { Header = Strings.L("Show the icon in the system tray"), IsOn = Prefs.Str("menubarStyle") != "off" };
                tray.Toggled += (_, _) => { if (tray.IsOn) Prefs.Reset("menubarStyle"); else Prefs.Set("menubarStyle", "off"); };
                Host.Children.Add(tray);
                break;
            case 4:
                var login = new ToggleSwitch { Header = Strings.L("Open at login"), IsOn = StartAtLogin.Enabled };
                login.Toggled += (_, _) => StartAtLogin.Set(login.IsOn);
                Host.Children.Add(login);
                break;
            case 5: Host.Children.Add(Toggle("Show notifications", "notify")); break;
        }
    }

    static ToggleSwitch Toggle(string header, string key)
    {
        var t = new ToggleSwitch { Header = Strings.L(header), IsOn = Prefs.Bool(key) };
        t.Toggled += (_, _) => Prefs.Set(key, t.IsOn);
        return t;
    }
}
