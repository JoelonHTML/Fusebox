using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Battery (only on PCs with a battery). The Mac's charge limit needs a helper that Windows has no general API for, so this page
/// points to Windows' own battery settings and to the PC maker's charge limit; Fusebox's battery options are on the Live page and Notifications.
/// </summary>
public sealed partial class BatterySettingsPage : Page
{
    public BatterySettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        Body.Children.Add(Section("Charge limit"));
        var open = new Button { Content = L("Open Battery settings") };
        open.Click += (_, _) => Kit.OpenUri("ms-settings:batterysaver");
        Body.Children.Add(Card("Limit charging", "Windows can't limit charging for every PC. If your PC maker offers a charge limit (often called Battery care or Battery Health Manager), you'll find it in their app.", open, "\uE857"));

        Body.Children.Add(Section("In Fusebox"));
        Body.Children.Add(Card("Battery alert", "Tells you when the battery is almost empty and not charging. Set the limit in Notifications.", Toggle("alert.battery.on"), "\uEA8F"));
    }
}
