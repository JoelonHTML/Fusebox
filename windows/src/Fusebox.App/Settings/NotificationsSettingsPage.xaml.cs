using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Notifications (FEATURES.md §9.12). Has (from v1): master switch, test notification.
/// To do (Settings agent): cooldown, per-kind rows (alert.*), sustained peak row, example toasts.
/// </summary>
public sealed partial class NotificationsSettingsPage : Page
{
    public NotificationsSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
        var test = new Button { Content = L("Send test notification"), IsEnabled = Prefs.Bool("notify") };
        test.Click += (_, _) => Notify.Send(L("Fusebox update available (test)"), L("This is how Fusebox tells you about a new version."));
        Body.Children.Add(Card("Show notifications", "", Toggle("notify", () => test.IsEnabled = Prefs.Bool("notify")), "\uEA8F"));
        Body.Children.Add(Card("Test", "If Focus assist is on, Windows keeps these notifications quiet too.", test, "\uE724"));
    }
}
