using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Settings;

/// <summary>Settings › Battery (FEATURES.md §9.14).</summary>
public sealed partial class BatterySettingsPage : Page
{
    public BatterySettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
    }
}
