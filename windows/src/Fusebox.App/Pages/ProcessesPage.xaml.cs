using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Pages;

public sealed partial class ProcessesPage : Page
{
    public ProcessesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
    }
}
