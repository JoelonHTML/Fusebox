using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.System;

namespace Fusebox.App.Settings;

/// <summary>A selectable card: theme tile, text size card, font card, tray style card, graphs card (Mac Card, §9.3).</summary>
[ContentProperty(Name = nameof(Child))]
public sealed partial class Tile : UserControl
{
    bool selected;

    public Tile()
    {
        InitializeComponent();
        Tapped += (_, _) => { Focus(FocusState.Pointer); Clicked?.Invoke(); };
        KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Space) { e.Handled = true; Clicked?.Invoke(); }
        };
    }

    public Tile(UIElement child, string name, Action click) : this()
    {
        Child = child;
        AutomationProperties.SetName(this, name);
        Clicked += click;
    }

    public event Action? Clicked;

    public UIElement? Child { get => (UIElement?)Host.Content; set => Host.Content = value; }

    public bool Selected
    {
        get => selected;
        set
        {
            selected = value;
            Ring.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(this, value ? Strings.L("Selected") : "");
        }
    }

    /// <summary>Dimmed and not clickable (the "Don't show" tray card without a taskbar button).</summary>
    public void SetEnabled(bool on) { IsEnabled = on; Opacity = on ? 1 : 0.4; }
}
