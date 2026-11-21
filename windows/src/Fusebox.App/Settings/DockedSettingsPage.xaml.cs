using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>Settings › Docked (FEATURES.md §9.10): ports, wireless, link speed unit.</summary>
public sealed partial class DockedSettingsPage : Page
{
    public DockedSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        Body.Children.Add(Section("Ports"));
        Body.Children.Add(Card("Show hubs & internals", "Also the hubs inside a dock and its Billboard part. Same as the button on the page.", Toggle("dockedInternals"), ""));
        Body.Children.Add(Card("List empty ports", "One line at the bottom, like \"USB-C 2 · USB-C 3 · empty\".", Toggle("dockedEmpty"), ""));
        Body.Children.Add(Card("Show displays", "External monitors under the port or DisplayLink dock that drives them, with resolution and refresh rate.", Toggle("dockedDisplays"), ""));

        Body.Children.Add(Section("Wireless"));
        Body.Children.Add(Card("Show wireless", "Bluetooth devices with battery, and a row when your internet runs through a phone hotspot or mobile modem.", Toggle("dockedWireless"), ""));

        Body.Children.Add(Section("Speed"));
        var speed = Card("Link speed", "", SegmentedBool("dockedBytes", "Gbit/s", "GB/s"), "");
        speed.SetDescriptionText(L("Gbit/s shows 40 Gbit/s · GB/s shows 5 GB/s (handy to compare with a drive)"));
        Body.Children.Add(speed);
    }
}
