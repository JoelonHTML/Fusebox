using System.Diagnostics;
using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Fusebox.App.Settings;

/// <summary>Flow layout that wraps onto the next line (Mac NetFlow): chips, tiles of unequal width.</summary>
public sealed partial class WrapPanel : Panel
{
    public double HSpacing { get; set; } = 6;
    public double VSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(available.Width, double.PositiveInfinity));
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > available.Width) { y += row + VSpacing; x = 0; row = 0; }
            x += d.Width + HSpacing; row = Math.Max(row, d.Height); width = Math.Max(width, x - HSpacing);
        }
        return new Size(width, y + row);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, row = 0;
        foreach (var c in Children)
        {
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > finalSize.Width) { y += row + VSpacing; x = 0; row = 0; }
            c.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width + HSpacing; row = Math.Max(row, d.Height);
        }
        return finalSize;
    }
}

/// <summary>Small controls shared by the Settings pages and the setup: captions, segmented pickers, steppers, chips, live subscriptions.</summary>
internal static class Kit
{
    /// <summary>Explanatory line under a control (Mac .caption, secondary). Pass the text through L(…) at the call site so the translation check sees the key.</summary>
    public static TextBlock CaptionText(string translated) =>
        new() { Text = translated, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 0) };

    /// <summary>Segmented picker (SelectorBar) over (id, label) items; <paramref name="pick"/> gets the id of a user choice.</summary>
    public static SelectorBar Segmented((string Id, string Label)[] items, string selected, Action<string> pick)
    {
        var bar = new SelectorBar();
        SelectorBarItem? sel = null;
        foreach (var (id, label) in items)
        {
            var item = new SelectorBarItem { Text = label, Tag = id };
            bar.Items.Add(item);
            if (id == selected) sel = item;
        }
        bar.SelectedItem = sel ?? bar.Items.FirstOrDefault();
        bar.SelectionChanged += (s, _) => { if (s.SelectedItem?.Tag is string id) pick(id); };
        return bar;
    }

    /// <summary>Number field with + / − (Mac Stepper). Invalid text (NaN) is ignored.</summary>
    public static NumberBox Stepper(double min, double max, double step, double value, Action<double> change, double width = 118)
    {
        var box = new NumberBox
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 4, Value = Math.Clamp(value, min, max), Width = width,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
        };
        box.ValueChanged += (_, e) => { if (double.IsFinite(e.NewValue)) change(e.NewValue); };
        return box;
    }

    /// <summary>On/off chip (Mac Chips): capsule, check mark when on, plus when off.</summary>
    public static ToggleButton Chip(string label, bool on, Action<bool> set)
    {
        var icon = new FontIcon { Glyph = on ? "" : "", FontSize = 11 };
        var text = new TextBlock { Text = label, FontSize = 12 };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(icon);
        content.Children.Add(text);
        var chip = new ToggleButton { Content = content, IsChecked = on, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 4, 10, 4), MinHeight = 28 };
        chip.Click += (_, _) => { bool v = chip.IsChecked == true; icon.Glyph = v ? "" : ""; set(v); };
        return chip;
    }

    /// <summary>A wrapping row of chips: (id, label) items, <paramref name="isOn"/> reads the current state, <paramref name="set"/> writes it.</summary>
    public static WrapPanel Chips((string Id, string Label)[] items, Func<string, bool> isOn, Action<string, bool> set)
    {
        var w = new WrapPanel();
        foreach (var (id, label) in items) w.Children.Add(Chip(label, isOn(id), v => set(id, v)));
        return w;
    }

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>Calls <paramref name="h"/> (on the UI thread) for every pref change while the element is on screen.</summary>
    public static void WhilePrefs(FrameworkElement el, Action<string> h)
    {
        void On(string k) => el.DispatcherQueue?.TryEnqueue(() => h(k));
        el.Loaded += (_, _) => { Prefs.Changed -= On; Prefs.Changed += On; };
        el.Unloaded += (_, _) => Prefs.Changed -= On;
    }

    /// <summary>Calls <paramref name="h"/> after the Fusebox theme changed while the element is on screen (previews re-render).</summary>
    public static void WhileTheme(FrameworkElement el, Action h)
    {
        el.Loaded += (_, _) => { ThemeService.ThemeChanged -= h; ThemeService.ThemeChanged += h; };
        el.Unloaded += (_, _) => ThemeService.ThemeChanged -= h;
    }

    /// <summary>Opens a link or a Windows Settings page (ms-settings:…) in its own app.</summary>
    public static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Link-style button that opens <paramref name="uri"/>; <paramref name="text"/> is already translated.</summary>
    public static HyperlinkButton Link(string text, string uri)
    {
        var b = new HyperlinkButton { Content = text, Padding = new Thickness(0, 2, 0, 2) };
        b.Click += (_, _) => OpenUri(uri);
        return b;
    }

    /// <summary>Windows accent as text color on the current scheme (badges): light accent in dark mode, dark accent in light mode.</summary>
    public static Windows.UI.Color AccentText()
    {
        try
        {
            var ui = new UISettings();
            return ui.GetColorValue(ThemeService.Target == Core.Look.Scheme.Dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        }
        catch { return Microsoft.UI.Colors.DodgerBlue; }
    }

    public static Windows.UI.Color Accent()
    {
        try { return new UISettings().GetColorValue(UIColorType.Accent); } catch { return Microsoft.UI.Colors.DodgerBlue; }
    }
}
