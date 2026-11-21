using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Tray icon (Mac "Menu bar", FEATURES.md §9.11). Has (from v1): tray icon on/off (menubarStyle "off" ↔ default "text").
/// To do (Settings agent): style cards (text/compact/bars/graph/icon/off), split, metrics (max 4), click type, rich panel, menu parts.
/// </summary>
public sealed partial class TraySettingsPage : Page
{
    public TraySettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
        var on = new ToggleSwitch { IsOn = Prefs.Str("menubarStyle") != "off" };
        on.Toggled += (_, _) => { if (on.IsOn) Prefs.Reset("menubarStyle"); else Prefs.Set("menubarStyle", "off"); };
        Body.Children.Add(Card("Show the icon in the system tray", "", on, "\uE7C4"));
    }
}
