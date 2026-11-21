using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Pages;

public sealed partial class CheckupPage : Page
{
    public CheckupPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
    }
}
