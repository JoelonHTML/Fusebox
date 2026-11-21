using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Settings;

/// <summary>Settings › Network (FEATURES.md §9.9).</summary>
public sealed partial class NetworkSettingsPage : Page
{
    public NetworkSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
    }
}
