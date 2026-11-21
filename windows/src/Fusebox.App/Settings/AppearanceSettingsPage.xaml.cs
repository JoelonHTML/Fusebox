using System.Diagnostics;
using Fusebox.Core.Look;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Appearance (FEATURES.md §9.5). Has (from v1, plain pickers): style, mode, font, language + restart, always on top, open at
/// login, shortcut on/off. To do (Settings agent): theme tiles, text size cards, font cards, accent picker, number style, hotkey recorder,
/// taskbar button toggle (dockIcon), keep running.
/// </summary>
public sealed partial class AppearanceSettingsPage : Page
{
    public AppearanceSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
        // Theme and font names are never translated.
        Body.Children.Add(Section("Style"));
        Body.Children.Add(Card("Style", "", Choice("theme", Themes.All.Select(t => (t.Id, t.Name)).ToArray()), "\uE771"));
        Body.Children.Add(Card("Mode", "", Choice("appearanceMode", [("system", L("System")), ("light", L("Light")), ("dark", L("Dark"))]), "\uE793"));
        Body.Children.Add(Card("Font", "", Choice("font",
            [("style", L("Matches style")), ("sfPro", "Segoe UI Variable"), ("rounded", "Nunito"), ("mono", "Cascadia Mono"),
             ("serif", "Georgia"), ("montserrat", "Montserrat"), ("ubuntu", "Ubuntu")]), "\uE8D2"));

        Body.Children.Add(Section("Window"));
        var restart = new Button { Content = L("Restart Fusebox"), Visibility = Visibility.Collapsed };
        restart.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false });
            App.Quit();
        };
        var startLanguage = Prefs.Str("appLanguage");
        var language = Choice("appLanguage", [("", L("System")), ("en", "English"), ("nl", "Nederlands"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español")],
            () => restart.Visibility = Prefs.Str("appLanguage") != startLanguage ? Visibility.Visible : Visibility.Collapsed);
        var pick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pick.Children.Add(restart);
        pick.Children.Add(language);
        Body.Children.Add(Card("Language", "Applies after a restart of Fusebox.", pick, "\uF2B7"));
        Body.Children.Add(Card("Always on top", "", Toggle("onTop"), "\uE718"));

        var login = new ToggleSwitch { IsOn = StartAtLogin.Enabled };
        login.Toggled += (_, _) => StartAtLogin.Set(login.IsOn);
        Body.Children.Add(Card("Open at login", "", login, "\uE7E8"));

        var hotkeyCard = Card("Shortcut to show or hide Fusebox", "", null, "\uE765");
        void Note() => hotkeyCard.SetDescriptionText(Prefs.Int("hotkeyCode") > 0 && !Hotkey.Registered
            ? L("Another app already uses this shortcut.") : $"{Prefs.Str("hotkeyLabel")} · {L("Works from any app.")}");
        var hotkey = new ToggleSwitch { IsOn = Prefs.Int("hotkeyCode") > 0 };
        hotkey.Toggled += (_, _) =>
        {
            if (hotkey.IsOn) Prefs.Reset("hotkeyCode"); else Prefs.Set("hotkeyCode", -1);
            DispatcherQueue.TryEnqueue(Note);   // Hotkey re-registers on the same queue first
        };
        hotkeyCard.Action = hotkey;
        Note();
        Body.Children.Add(hotkeyCard);
    }
}
