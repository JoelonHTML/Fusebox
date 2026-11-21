using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Settings;

/// <summary>Settings › Checkup (FEATURES.md §9.8).</summary>
public sealed partial class CheckupSettingsPage : Page
{
    public CheckupSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
    }
}
