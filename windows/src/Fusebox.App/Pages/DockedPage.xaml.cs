using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Pages;

public sealed partial class DockedPage : Page
{
    public DockedPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
    }
}
