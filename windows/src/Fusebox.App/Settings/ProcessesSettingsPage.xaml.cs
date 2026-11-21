using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Settings;

/// <summary>Settings › Processes (FEATURES.md §9.7).</summary>
public sealed partial class ProcessesSettingsPage : Page
{
    public ProcessesSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
    }
}
