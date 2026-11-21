using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
}
