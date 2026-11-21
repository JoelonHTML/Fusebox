using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
    }
}
