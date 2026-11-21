using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Pages;

public sealed partial class NetworkPage : Page
{
    public NetworkPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
    }
}
