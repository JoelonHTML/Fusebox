using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Profile (FEATURES.md §9.4). Has (from v1): Low impact picker, Run setup again.
/// To do (Settings agent): 4 profile cards (Prefs.Store.ApplyProfile), share/import (Prefs.Store.Export / ImportFile), reset everything.
/// </summary>
public sealed partial class ProfileSettingsPage : Page
{
    SettingsCard? low;

    public ProfileSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
        low = Card("Low impact", "Fusebox measures every 5 seconds instead of 2, without glass effects, and Windows runs it in Efficiency mode. Automatic: on in Energy saver, or on battery below 20%.",
            Choice("lowImpact", [("off", L("Off")), ("on", L("On")), ("auto", L("Automatic"))]), "\uEC0A");
        Body.Children.Add(low);
        var setup = new Button { Content = L("Run setup again") };
        setup.Click += (_, _) => Boot.ShowSetup();
        Body.Children.Add(Card("Run setup again", "", setup, "\uE8B5"));
        ShowLow();
        Loaded += (_, _) => LowImpact.Changed += OnLow;
        Unloaded += (_, _) => LowImpact.Changed -= OnLow;
    }

    void OnLow() => DispatcherQueue.TryEnqueue(ShowLow);
    void ShowLow() { if (low != null) low.Header = LowImpact.On ? "Low impact · on now" : "Low impact"; }
}
