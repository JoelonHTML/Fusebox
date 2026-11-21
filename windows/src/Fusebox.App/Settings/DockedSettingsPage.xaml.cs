using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Settings;

/// <summary>Settings › Docked (FEATURES.md §9.10).</summary>
public sealed partial class DockedSettingsPage : Page
{
    public DockedSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
    }
}
