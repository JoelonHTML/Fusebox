using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Small builders for Settings pages written in code, in the Windows 11 Settings look: cards (rows), sections, blocks (a card holding tiles
/// or a preview), expanders (a row with sub-options), switches with "On"/"Off" on their left, drop-downs bound to a pref key.
/// </summary>
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

    /// <summary>A card that only holds <paramref name="content"/> (a tile grid, a preview, a list), padded like a row.</summary>
    public static SettingsCard Block(UIElement content) => new() { Below = content };

    /// <summary>
    /// Windows' expandable row (SettingsExpander): <paramref name="head"/> is the row (icon, header, description, control), the items are the
    /// sub-options shown when it opens, lined up with the header text. Built on the WinUI Expander, so it looks and animates like Windows.
    /// </summary>
    public static Expander Expand(SettingsCard head, params UIElement[] items)
    {
        head.MakePlain(64);
        var body = new StackPanel { Spacing = 6, Margin = new Thickness(38, 0, 44, 0) };
        foreach (var i in items) body.Children.Add(i);
        return new Expander
        {
            Header = head, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    /// <summary>A row inside an expander: no card of its own, lower.</summary>
    public static SettingsCard Item(SettingsCard row)
    {
        row.MakePlain(40);
        return row;
    }

    /// <summary>
    /// Expander with a check box per item (Columns, Sections) instead of chips; the header says how many are on. <paramref name="isOn"/> reads,
    /// <paramref name="set"/> writes; <paramref name="more"/> (a preview, a note) follows the check boxes.
    /// </summary>
    public static Expander CheckList(string header, string glyph, (string Id, string Label)[] items, Func<string, bool> isOn, Action<string, bool> set,
        params UIElement[] more)
    {
        var head = new SettingsCard { Header = header, Glyph = glyph };
        void Count() => head.SetDescriptionText(L("%lld of %lld shown", items.Count(i => isOn(i.Id)), items.Length));
        var checks = new WrapPanel { HSpacing = 8, VSpacing = 0, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (id, label) in items)
        {
            var c = new CheckBox { Content = label, IsChecked = isOn(id), Width = 200 };
            c.Click += (_, _) => { set(id, c.IsChecked == true); Count(); };
            checks.Children.Add(c);
        }
        Count();
        return Expand(head, [checks, .. more]);
    }

    /// <summary>Check boxes over a list pref that stores what is OFF (new items default to on): procHide, checkupHide, netHide.</summary>
    public static Expander HiddenList(string header, string glyph, string key, (string Id, string Label)[] items, params UIElement[] more) =>
        CheckList(header, glyph, items, id => !Prefs.List(key).Contains(id), (id, on) =>
        {
            var l = Prefs.List(key).Where(x => x != id).ToList();
            if (!on) l.Add(id);
            Prefs.SetList(key, l);
        }, more);

    /// <summary>A switch bound to a bool pref (table default when unset).</summary>
    public static ToggleSwitch Toggle(string key, Action? after = null)
    {
        var t = new ToggleSwitch { IsOn = Prefs.Bool(key), MinWidth = 0 };
        t.Toggled += (_, _) => { Prefs.Set(key, t.IsOn); after?.Invoke(); };
        return t;
    }

    /// <summary>
    /// A switch the Windows Settings way: "On"/"Off" left of the switch, the switch flush right. A card does this to every ToggleSwitch it gets.
    /// </summary>
    public static UIElement Switch(ToggleSwitch t)
    {
        var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        void Show() { state.Text = t.IsOn ? L("On") : L("Off"); state.Opacity = t.IsEnabled ? 1 : 0.45; }
        t.OnContent = null;
        t.OffContent = null;
        t.MinWidth = 0;
        t.Margin = new Thickness(0, 0, -12, 0);   // the template keeps 12 px after the switch for its own (now empty) text
        t.Toggled += (_, _) => Show();
        t.IsEnabledChanged += (_, _) => Show();
        Show();
        return Kit.Row(12, state, t);
    }

    /// <summary>A drop-down bound to a string pref: (stored id, label shown) pairs.</summary>
    public static ComboBox Choice(string key, (string Id, string Label)[] items, Action? after = null) =>
        Kit.Pick(items, Prefs.Str(key), id => { Prefs.Set(key, id); after?.Invoke(); });

    /// <summary>A drop-down bound to a bool pref: the first label = false, the second = true.</summary>
    public static ComboBox ChoiceBool(string key, string off, string on, Action? after = null) =>
        Kit.Pick([("0", off), ("1", on)], Prefs.Bool(key) ? "1" : "0", id => { Prefs.Set(key, id == "1"); after?.Invoke(); });

    /// <summary>A drop-down bound to an integer pref: (value, label shown) pairs.</summary>
    public static ComboBox LongChoice(string key, (long Value, string Label)[] items, Action? after = null) =>
        Kit.Pick(items.Select(i => (i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), i.Label)).ToArray(), Prefs.Long(key).ToString(System.Globalization.CultureInfo.InvariantCulture),
            id => { Prefs.Set(key, long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)); after?.Invoke(); });

    /// <summary>A confirmation dialog (title, message, primary button, Cancel). True when the primary button was pressed.</summary>
    public static async Task<bool> Confirm(XamlRoot root, string title, string message, string primary)
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = message, PrimaryButtonText = primary, CloseButtonText = L("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,   // the dialog sits outside the window's tree
        };
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out var style) && style is Style s) d.Style = s;
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Explanatory line under a group of cards (already translated).</summary>
    public static void Footer(Panel body, string translated) => body.Children.Add(Kit.CaptionText(translated));
}
