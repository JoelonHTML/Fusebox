using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Live page (FEATURES.md §9.6). Has (from v1): the measurement list (MetricsEditor, arrows instead of drag).
/// To do (Settings agent): live preview, graphs, units, thresholds, small-window options.
/// </summary>
public sealed partial class LiveSettingsPage : Page
{
    public LiveSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
        Body.Children.Add(Section("Measurements"));
        Body.Children.Add(MetricsEditor.Create());
    }
}
