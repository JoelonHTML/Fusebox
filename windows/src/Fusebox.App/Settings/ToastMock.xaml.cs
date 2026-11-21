using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Settings;

/// <summary>A mini notification showing what an alert will look like (title and text already translated).</summary>
public sealed partial class ToastMock : UserControl
{
    public ToastMock(string title, string body)
    {
        InitializeComponent();
        Set(title, body);
    }

    public void Set(string title, string body)
    {
        TitleText.Text = title;
        BodyText.Text = body;
        AutomationProperties.SetName(this, Strings.L("Example notification: %@. %@", title, body));
    }
}
