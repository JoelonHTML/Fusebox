using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Fusebox.App.Settings;

/// <summary>
/// A Settings row like Windows 11's SettingsCard (no CommunityToolkit): Glyph (Segoe Fluent, optional), Header, Description (optional), the
/// trailing control as content, and optional <see cref="Below"/> content (tiles, a preview) inside the same card. Header/Description are
/// English keys, translated here (Strings.L). A ToggleSwitch gets Windows' "On"/"Off" text to its left (see <see cref="SettingsUi.Switch"/>).
/// XAML: <c>&lt;s:SettingsCard Glyph="&amp;#xE7E8;" Header="Keep measuring when the window is closed" Description="…"&gt;&lt;ToggleSwitch /&gt;&lt;/s:SettingsCard&gt;</c>
/// Code: <see cref="SettingsUi"/>. Section header above a group: <c>&lt;TextBlock Style="{StaticResource SettingsSectionHeader}" Text="Window" /&gt;</c>.
/// </summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class SettingsCard : UserControl
{
    string header = "", description = "", glyph = "";
    object? action;

    public SettingsCard()
    {
        InitializeComponent();
        // Windows dims the whole row when it can't be used; the controls dim themselves, the texts and icon need it here.
        IsEnabledChanged += (_, _) => { Texts.Opacity = Icon.Opacity = IsEnabled ? 1 : 0.45; };
    }

    public string Header
    {
        get => header;
        set { header = value; HeaderText.Text = Strings.L(value); Layout(); }
    }

    public string Description
    {
        get => description;
        set
        {
            description = value;
            DescriptionText.Text = Strings.L(value);
            DescriptionText.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Segoe Fluent Icons glyph, e.g. "". Empty = no icon column.</summary>
    public string Glyph
    {
        get => glyph;
        set { glyph = value; Icon.Glyph = value; Icon.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
    }

    /// <summary>The trailing control (ToggleSwitch, ComboBox, Button …).</summary>
    public object? Action
    {
        get => action;
        set
        {
            action = value;
            ActionHost.Content = value is ToggleSwitch t ? SettingsUi.Switch(t) : value;
            Layout();
        }
    }

    /// <summary>Content inside the card under the row (tile grid, preview). A card without header and action shows only this.</summary>
    public UIElement? Below
    {
        get => (UIElement?)BelowHost.Content;
        set { BelowHost.Content = value; Layout(); }
    }

    /// <summary>
    /// No card of its own: the header of an expander (<see cref="SettingsUi.Expand"/>) or a row inside one.
    /// </summary>
    internal void MakePlain(double minHeight)
    {
        Frame.Background = null;
        Frame.BorderThickness = new Thickness(0);
        Frame.Padding = new Thickness(0);
        Head.MinHeight = minHeight;
    }

    /// <summary>Description already translated / built in code (no Strings.L), e.g. with a number in it.</summary>
    public void SetDescriptionText(string text)
    {
        DescriptionText.Text = text;
        DescriptionText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Header already translated / built in code.</summary>
    public void SetHeaderText(string text) { HeaderText.Text = text; Layout(); }

    void Layout()
    {
        bool head = HeaderText.Text.Length > 0 || action != null;
        Head.Visibility = head ? Visibility.Visible : Visibility.Collapsed;
        BelowHost.Visibility = BelowHost.Content != null ? Visibility.Visible : Visibility.Collapsed;
        BelowHost.Margin = new Thickness(0, head ? 0 : 16, 0, 16);
        // The trailing control is announced with the row's header (a switch without its own text, a drop-down).
        if (action is ToggleSwitch or ComboBox or Slider or NumberBox or TextBox && HeaderText.Text.Length > 0)
            AutomationProperties.SetName((DependencyObject)action, HeaderText.Text);
    }
}
