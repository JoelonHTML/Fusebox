using System.Diagnostics;
using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Fusebox.App.Settings;

/// <summary>Flow layout that wraps onto the next line (Mac NetFlow): check boxes of a column list.</summary>
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

/// <summary>Small controls shared by the Settings pages and the setup: captions, drop-downs, steppers, live subscriptions.</summary>
internal static class Kit
{
    /// <summary>Explanatory line under a control (Mac .caption, secondary). Pass the text through L(…) at the call site so the translation check sees the key.</summary>
    public static TextBlock CaptionText(string translated) =>
        new() { Text = translated, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 2, 0) };

    /// <summary>Drop-down over (id, label) items (Windows Settings uses a ComboBox for a choice); <paramref name="pick"/> gets the id of a user choice.</summary>
    public static ComboBox Pick((string Id, string Label)[] items, string selected, Action<string> pick)
    {
        var box = new ComboBox { MinWidth = 180 };
        foreach (var (_, label) in items) box.Items.Add(label);
        box.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.Id == selected));
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) pick(items[box.SelectedIndex].Id); };
        return box;
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
