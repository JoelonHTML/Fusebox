using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Fusebox.App.Settings;

/// <summary>
/// A Settings row (no CommunityToolkit): Glyph (Segoe Fluent, optional), Header, Description (optional), and the trailing control as
/// content. Header/Description are English keys, translated here (Strings.L).
/// XAML: <c>&lt;s:SettingsCard Glyph="&amp;#xE7E8;" Header="Keep measuring when the window is closed" Description="…"&gt;&lt;ToggleSwitch /&gt;&lt;/s:SettingsCard&gt;</c>
/// Code: <see cref="SettingsUi"/>. Section header above a group: <c>&lt;TextBlock Style="{StaticResource SettingsSectionHeader}" Text="Window" /&gt;</c>.
/// </summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class SettingsCard : UserControl
{
    string header = "", description = "", glyph = "";

    public SettingsCard() => InitializeComponent();

    public string Header
    {
        get => header;
        set { header = value; HeaderText.Text = Strings.L(value); }
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

    /// <summary>Segoe Fluent Icons glyph, e.g. "\uE713". Empty = no icon column.</summary>
    public string Glyph
    {
        get => glyph;
        set { glyph = value; Icon.Glyph = value; Icon.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
    }

    /// <summary>The trailing control (ToggleSwitch, ComboBox, Button …).</summary>
    public object? Action { get => ActionHost.Content; set => ActionHost.Content = value; }

    /// <summary>Description already translated / built in code (no Strings.L), e.g. with a number in it.</summary>
    public void SetDescriptionText(string text)
    {
        DescriptionText.Text = text;
        DescriptionText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
