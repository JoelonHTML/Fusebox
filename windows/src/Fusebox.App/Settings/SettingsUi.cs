using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>Small builders for Settings pages written in code: rows, toggles and pickers bound to a pref key.</summary>
public static class SettingsUi
{
    /// <summary>Translates the page's XAML text once, when it first loads (Loc.Apply).</summary>
    public static void TranslateOnce(Page page)
    {
        void Once(object s, RoutedEventArgs e) { page.Loaded -= Once; Loc.Apply(page); }
        page.Loaded += Once;
    }

    /// <summary>A row; header/description are English keys (translated by the card).</summary>
    public static SettingsCard Card(string header, string description, object? action, string glyph = "") =>
        new() { Header = header, Description = description, Action = action, Glyph = glyph };

    /// <summary>Section header text above a group of cards (English key).</summary>
    public static TextBlock Section(string title) => new() { Text = Strings.L(title), Style = (Style)Application.Current.Resources["SettingsSectionHeader"] };

    /// <summary>A switch bound to a bool pref (table default when unset).</summary>
    public static ToggleSwitch Toggle(string key, Action? after = null)
    {
        var t = new ToggleSwitch { IsOn = Prefs.Bool(key), MinWidth = 0 };
        t.Toggled += (_, _) => { Prefs.Set(key, t.IsOn); after?.Invoke(); };
        return t;
    }

    /// <summary>A drop-down bound to a string pref: (stored id, label shown) pairs.</summary>
    public static ComboBox Choice(string key, (string Id, string Label)[] items, Action? after = null)
    {
        var box = new ComboBox { MinWidth = 180 };
        foreach (var (_, label) in items) box.Items.Add(label);
        box.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.Id == Prefs.Str(key)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            Prefs.Set(key, items[box.SelectedIndex].Id);
            after?.Invoke();
        };
        return box;
    }

    /// <summary>A drop-down bound to an integer pref: (value, label shown) pairs.</summary>
    public static ComboBox LongChoice(string key, (long Value, string Label)[] items, Action? after = null)
    {
        var box = new ComboBox { MinWidth = 180 };
        foreach (var (_, label) in items) box.Items.Add(label);
        box.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.Value == Prefs.Long(key)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            Prefs.Set(key, items[box.SelectedIndex].Value);
            after?.Invoke();
        };
        return box;
    }

    /// <summary>Segmented picker bound to a string pref: (stored id, label shown) pairs.</summary>
    public static SelectorBar Segmented(string key, (string Id, string Label)[] items, Action? after = null) =>
        Kit.Segmented(items, Prefs.Str(key), id => { Prefs.Set(key, id); after?.Invoke(); });

    /// <summary>Segmented picker bound to a bool pref: the first label = false, the second = true.</summary>
    public static SelectorBar SegmentedBool(string key, string off, string on, Action? after = null) =>
        Kit.Segmented([("0", off), ("1", on)], Prefs.Bool(key) ? "1" : "0", id => { Prefs.Set(key, id == "1"); after?.Invoke(); });

    /// <summary>Wrapping chips over a list pref that stores what is OFF (new items default to on): procHide, checkupHide, netHide.</summary>
    public static WrapPanel HiddenChips(string key, (string Id, string Label)[] items) =>
        Kit.Chips(items, id => !Prefs.List(key).Contains(id), (id, on) =>
        {
            var l = Prefs.List(key).Where(x => x != id).ToList();
            if (!on) l.Add(id);
            Prefs.SetList(key, l);
        });

    /// <summary>Destructive-looking button (red text on both light and dark).</summary>
    public static Button Destructive(string text)
    {
        var red = ThemeService.Target == Core.Look.Scheme.Dark ? Windows.UI.Color.FromArgb(255, 255, 153, 164) : Windows.UI.Color.FromArgb(255, 196, 43, 28);
        return new Button { Content = text, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(red) };
    }

    /// <summary>A confirmation dialog (title, message, primary button, Cancel). True when the primary button was pressed.</summary>
    public static async Task<bool> Confirm(XamlRoot root, string title, string message, string primary)
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = message, PrimaryButtonText = primary, CloseButtonText = L("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Space between a group of cards and the next section / footer caption.</summary>
    public static void Footer(Panel body, string translated) => body.Children.Add(Kit.CaptionText(translated));
}
