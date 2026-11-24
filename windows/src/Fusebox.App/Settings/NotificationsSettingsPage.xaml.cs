using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Notifications (FEATURES.md §9.12): master switch, how often the same kind may repeat, a test, and one row per kind
/// (limit, minutes in a row, an example notification) plus the sustained peak. The rules live in Core/Alerts, the runner in AlertRunner.
/// </summary>
public sealed partial class NotificationsSettingsPage : Page
{
    public NotificationsSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
        var kinds = new StackPanel { Spacing = 4 };
        var kindsHost = new ContentControl { Content = kinds, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var cooldown = new SettingsCard { Glyph = "" };
        var test = new Button { Content = L("Send test notification") };
        var blocked = new InfoBar
        {
            Severity = InfoBarSeverity.Warning, IsOpen = false, IsClosable = false, Message = L("Windows is keeping Fusebox's notifications off."),
            ActionButton = new HyperlinkButton { Content = L("Open Windows notification settings"), NavigateUri = new Uri("ms-settings:notifications") },
        };

        void Master()
        {
            bool on = Prefs.Bool("notify");
            cooldown.IsEnabled = on; test.IsEnabled = on; kindsHost.IsEnabled = on;
            blocked.IsOpen = on && !Notify.Allowed;
        }
        Body.Children.Add(Card("Show notifications", "", Toggle("notify", Master), ""));
        Body.Children.Add(blocked);

        void Cooldown() => cooldown.Header = L("Same kind at most once every %lld min", Prefs.Long("alertCooldown"));
        cooldown.Action = Kit.Stepper(5, 240, 5, Prefs.Long("alertCooldown"), v => { Prefs.Set("alertCooldown", (long)v); Cooldown(); });
        Cooldown();
        Body.Children.Add(cooldown);

        test.Click += (_, _) => Notify.SendTest();
        Body.Children.Add(Card("Test", "No notification? Allow Fusebox in Windows Settings › System › Notifications.", test, ""));

        Body.Children.Add(Section("Per kind: limit and how many minutes in a row"));
        foreach (var id in Parts.AlertIds()) kinds.Children.Add(Parts.AlertRow(id));
        kinds.Children.Add(Parts.PeakRow());
        Body.Children.Add(kindsHost);

        Footer(Body, L("If Focus or Do Not Disturb is on, Windows keeps these notifications quiet too."));
        Body.Children.Add(Kit.Link(L("Open Focus settings"), "ms-settings:quietmoments"));
        Master();
    }
}
