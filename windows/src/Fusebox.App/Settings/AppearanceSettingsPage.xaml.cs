using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Appearance (FEATURES.md §9.5): mode, six theme tiles, text size, seven fonts, accent color, number changes, and the
/// window section (language, always on top, keep measuring, shortcut, taskbar button, open at login).
/// </summary>
public sealed partial class AppearanceSettingsPage : Page
{
    public AppearanceSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        Body.Children.Add(Section("Style"));
        Body.Children.Add(Card("Mode", "", Segmented("appearanceMode", [("system", L("System")), ("light", L("Light")), ("dark", L("Dark"))]), ""));
        Body.Children.Add(Parts.ThemeTiles());

        Body.Children.Add(Section("Text size"));
        Body.Children.Add(Parts.TextSizeCards());

        Body.Children.Add(Section("Font"));
        Body.Children.Add(Parts.FontCards());

        var accent = Card("Accent color", "", Parts.AccentPicker(), "");
        accent.Margin = new Thickness(0, 14, 0, 0);
        Body.Children.Add(accent);
        var numbers = Card("Number changes", "", Kit.Row(12, Previews.NumberDemo(),
            Segmented("numberStyle", [("roll", L("Roll")), ("fade", L("Fade")), ("instant", L("Instant"))])), "");
        ToolTipService.SetToolTip(numbers, L("Roll: digits flip from top to bottom · Fade: a short crossfade · Instant: no animation"));
        Body.Children.Add(numbers);

        Body.Children.Add(Section("Window"));
        Body.Children.Add(LanguageCard());
        Body.Children.Add(Card("Always on top", "", Toggle("onTop"), ""));
        Body.Children.Add(Card("Keep measuring when the window is closed", "So Checkup always has data. Quit from the tray menu to stop completely.", Toggle("keepRunning"), ""));

        var hotkey = Card("Shortcut to show or hide Fusebox", "", null, "");
        var conflict = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = false, IsClosable = false, Message = L("Another app already uses this shortcut.") };
        hotkey.Action = HotkeyRecorder.Create(hotkey, conflict);
        Body.Children.Add(hotkey);
        Body.Children.Add(conflict);

        Body.Children.Add(Parts.TaskbarCard());

        var login = new ToggleSwitch { IsOn = StartAtLogin.Enabled, MinWidth = 0 };
        login.Toggled += (_, _) => StartAtLogin.Set(login.IsOn);
        Body.Children.Add(Card("Open at login", "", login, ""));
        Footer(Body, L("Fusebox always opens on Live, at the size you last gave that window."));
    }

    /// <summary>Language in its own name (never translated) + "Applies after a restart" with a restart button.</summary>
    static SettingsCard LanguageCard()
    {
        var restart = new Button { Content = L("Restart Fusebox"), Visibility = Visibility.Collapsed };
        restart.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false });
            App.Quit();
        };
        var start = Prefs.Str("appLanguage");
        var language = Choice("appLanguage", [("", L("System")), ("en", "English"), ("nl", "Nederlands"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español")],
            () => restart.Visibility = Prefs.Str("appLanguage") != start ? Visibility.Visible : Visibility.Collapsed);
        return Card("Language", "Applies after a restart of Fusebox.", Kit.Row(8, restart, language), "");
    }
}
