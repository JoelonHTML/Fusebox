using Fusebox.App.Controls;
using Fusebox.Core;
using Fusebox.Core.Look;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>
/// The Checkup building blocks (§5.6.0 / §5.B.0) built in code: theme text colours, tones, Surface (group card), CardTitle, Headline,
/// Primary/Link buttons, Capped, mini bars. Checkup never uses the theme's row style: one card surface per group, items inside are
/// plain sections separated by spacing (no lines, no boxes). Everything reads the theme at build time; the page rebuilds on ThemeChanged.
/// </summary>
internal static class Ui
{
    public static Theme T => ThemeService.Current;
    public static bool Blocks => T.RowStyle == RowStyle.Blocks;
    public static Brush B(string key) => ThemeService.Brush(key);
    public static Brush OnRow(bool secondary = false) => B(secondary ? "FuseboxOnRowSecondary" : "FuseboxOnRow");

    /// <summary>Tone 0 normal (icons accent, headings text), 1 watch out, 2 serious, 3 good. White on Color blocks.</summary>
    public static Brush Tone(int t, bool icon = true) => Blocks ? OnRow() : t switch
    {
        1 => B("FuseboxWarn"), 2 => B("FuseboxDanger"), 3 => B("FuseboxGood"), _ => icon ? B("FuseboxAccent") : OnRow(),
    };

    public static Windows.UI.Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Microsoft.UI.Colors.Gray;

    // ---- text ----

    public static TextBlock Label(string text, double pt, bool secondary = false, bool bold = false, bool wrap = true)
    {
        var t = new TextBlock { Text = text, Foreground = OnRow(secondary), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        Look.Label(t, pt);
        if (bold) t.FontWeight = FontWeights.SemiBold;
        return t;
    }

    public static TextBlock Num(string text, double pt, bool secondary = false, Brush? color = null)
    {
        var t = new TextBlock { Text = text, Foreground = color ?? OnRow(secondary), TextWrapping = TextWrapping.NoWrap };
        Look.Number(t, pt);
        return t;
    }

    public static TextBlock Small(string text) => Label(text, 11, secondary: true);

    /// <summary>Zero state: one calm line with a check, instead of a big green "None" (empty things stay quiet).</summary>
    public static Grid Calm(string text)
    {
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(Icon("\uE73E", 11, Tone(3)));
        var t = Small(text); t.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(t, 1); g.Children.Add(t);
        return g;
    }

    public static FontIcon Icon(string glyph, double pt, Brush? color = null) =>
        new() { Glyph = glyph, FontSize = T.Size(pt), Foreground = color ?? Tone(0), VerticalAlignment = VerticalAlignment.Center };

    // ---- structure ----

    /// <summary>A group card (Fluent card feel in theme colours): radius 8, the theme card fill with a 1 px theme stroke; glass fill + rim in
    /// Liquid Glass, the block colour (index % 5, no stroke) in Color blocks, where text on it is white (OnRow).</summary>
    public static Border Surface(UIElement child, int index)
    {
        var b = new Border { Child = child, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch };
        switch (T.RowStyle)
        {
            case RowStyle.Blocks: b.Background = B($"FuseboxBlock{index % 5}"); break;
            case RowStyle.Glass: b.Background = B("FuseboxGlassFill"); b.BorderBrush = B("FuseboxGlassRim"); b.BorderThickness = new Thickness(1); break;
            default: b.Background = B("FuseboxCardFill"); b.BorderBrush = B("FuseboxLine"); b.BorderThickness = new Thickness(1); break;
        }
        return b;
    }

    /// <summary><paramref name="color"/> at <paramref name="opacity"/> (a soft tint for tips and tags).</summary>
    public static SolidColorBrush Tint(Brush color, double opacity) => new(ColorOf(color)) { Opacity = opacity };

    /// <summary>A borderless, stretched button that only shows the theme hover tint (group headers, disk rows). The template's states read
    /// these keys from the button's own resources, so no custom template is needed.</summary>
    public static void Flat(Button b)
    {
        var none = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        b.Background = none; b.BorderThickness = new Thickness(0); b.MinHeight = 0; b.MinWidth = 0;
        b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        foreach (var s in new[] { "", "PointerOver", "Pressed" })
        {
            b.Resources["ButtonBackground" + s] = s.Length == 0 ? none : B("FuseboxHover");
            b.Resources["ButtonBorderBrush" + s] = none;
        }
    }

    public static StackPanel Stack(double spacing = 5, params UIElement[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    /// <summary>Section title (semibold 13, wraps) and an optional trailing element pushed to the right. The glyph shows only for a real
    /// problem (tone 1 watch out / 2 serious), in that colour; calm sections have no icon.</summary>
    public static Grid CardTitle(string glyph, string title, int tone = 0, UIElement? trailing = null)
    {
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        if (tone is 1 or 2) g.Children.Add(Icon(glyph, 12, Tone(tone)));
        var t = Label(title, 13, bold: true);
        t.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        if (trailing != null) { Grid.SetColumn((FrameworkElement)trailing, 2); g.Children.Add(trailing); }
        return g;
    }

    public static StackPanel Headline(string big, string? small = null, int tone = 0)
    {
        var s = new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 0, 2) };
        var b = Num(big, 20, color: Tone(tone, false));
        b.TextTrimming = TextTrimming.CharacterEllipsis;
        s.Children.Add(b);
        if (!string.IsNullOrEmpty(small)) s.Children.Add(Small(small));
        return s;
    }

    /// <summary>Name left (trimmed), detail right (number font, secondary).</summary>
    public static Grid ItemRow(string name, string detail, double detailPt = 10, bool detailSecondary = true)
    {
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var n = Label(name, 12, wrap: false);
        g.Children.Add(n);
        var d = Num(detail, detailPt, secondary: detailSecondary);
        d.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(d, 1);
        g.Children.Add(d);
        return g;
    }

    public static Grid LineRow(string label, string value)
    {
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.Children.Add(Label(label, 11, secondary: true));
        var v = Num(value, 11);
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    public static UIElement Sub(string text) { var t = Label(text, 10, secondary: true); t.Margin = new Thickness(0, 6, 0, 0); return t; }

    // ---- controls ----

    /// <summary>The one primary action of a group ("Check now"): Fluent AccentButtonStyle, small, in the theme accent (white on Color
    /// blocks) with readable text. <paramref name="text"/> is already translated.</summary>
    public static Button Primary(string text, Action click)
    {
        var bg = Blocks ? OnRow() : B("FuseboxAccent");
        var c = ColorOf(bg);
        Brush fg = new SolidColorBrush(0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150 ? Windows.UI.Color.FromArgb(255, 26, 26, 26) : Microsoft.UI.Colors.White);
        var b = new Button
        {
            Content = text, Style = (Style)Application.Current.Resources["AccentButtonStyle"], Background = bg, Foreground = fg,
            Padding = new Thickness(12, 4, 12, 5), MinHeight = 0, MinWidth = 0, FontSize = T.Size(12), VerticalAlignment = VerticalAlignment.Center,
        };
        // The template's hover/pressed states read these keys from the button's own resources (lightweight styling, as on the Network page).
        b.Resources["AccentButtonBackground"] = bg;
        b.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(c) { Opacity = 0.9 };
        b.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(c) { Opacity = 0.8 };
        foreach (var s in new[] { "", "PointerOver", "Pressed" }) b.Resources["AccentButtonForeground" + s] = fg;
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Secondary action as a text link (HyperlinkButton) in the theme accent; <paramref name="danger"/> = a destructive action
    /// (Stop) in the danger colour. Texts are already translated (call sites write L("…") so the translation check sees them).</summary>
    public static HyperlinkButton Link(string text, Action click, string? tooltip = null, bool danger = false)
    {
        var fg = Tone(danger ? 2 : 0);
        var b = new HyperlinkButton
        {
            Content = text, Foreground = fg, FontSize = T.Size(12), FontFamily = ThemeService.Family(T.Face), Padding = new Thickness(0, 2, 0, 2),
            MinHeight = 0, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var s in new[] { "", "PointerOver", "Pressed" }) b.Resources["HyperlinkButtonForeground" + s] = fg;
        b.Click += (_, _) => click();
        if (tooltip != null) ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    /// <summary>Refresh icon button for the power cards.</summary>
    public static Button IconButton(string glyph, string tooltip, Action click)
    {
        var b = new Button { Content = Icon(glyph, 11, OnRow(true)), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(4), MinHeight = 0, MinWidth = 0 };
        b.Click += (_, _) => click();
        ToolTipService.SetToolTip(b, tooltip);
        AutomationProperties(b, tooltip);
        return b;
    }

    static void AutomationProperties(DependencyObject o, string name) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(o, name);

    public static ProgressRing Spinner() => new() { Width = 16, Height = 16, IsActive = true, HorizontalAlignment = HorizontalAlignment.Left };

    public static ProgressBar Progress(double value, double max = 1) =>
        new() { Minimum = 0, Maximum = max, Value = value, Height = 4, Foreground = Blocks ? OnRow() : B("FuseboxAccent") };

    /// <summary>"can go" / "check first" / "keep" / "?" badge: semibold 10 text in its colour on a 15 % tint of it, radius 4, no stroke.</summary>
    public static Border Tag(string text, Brush color) => new()
    {
        Child = new TextBlock { Text = text, Foreground = color, FontSize = T.Size(10), FontWeight = FontWeights.SemiBold },
        Background = Tint(color, 0.15), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A bar of <paramref name="width"/> × 4 whose fill is <paramref name="v"/> (0–1) of it.</summary>
    public static Grid Bar(double v, double width = 50, double min = 0.04)
    {
        var accent = Blocks ? OnRow() : B("FuseboxAccent");
        var g = new Grid { Width = width, Height = 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        g.Children.Add(new Border { Background = accent, Opacity = 0.18, CornerRadius = new CornerRadius(2) });
        g.Children.Add(new Border { Background = accent, Opacity = 0.7, CornerRadius = new CornerRadius(2), Width = width * Math.Clamp(v, min, 1), HorizontalAlignment = HorizontalAlignment.Left });
        return g;
    }

    /// <summary>First <paramref name="limit"/> rows, then "Show all (N)" / "Show less" (only when more than limit + 1).</summary>
    public static StackPanel Capped(IReadOnlyList<UIElement> rows, int limit = 5, double spacing = 4)
    {
        var s = new StackPanel { Spacing = spacing };
        bool capped = rows.Count > limit + 1;
        bool all = false;
        void Apply()
        {
            for (int i = 0; i < rows.Count; i++) rows[i].Visibility = !capped || all || i < limit ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var r in rows) s.Children.Add(r);
        if (capped)
        {
            HyperlinkButton? btn = null;
            btn = Link(L("Show all (%lld)", rows.Count), () =>
            {
                all = !all;
                btn!.Content = all ? L("Show less") : L("Show all (%lld)", rows.Count);
                Apply();
            });
            s.Children.Add(btn);
        }
        Apply();
        return s;
    }

    public static void Tip(FrameworkElement e, string text) => ToolTipService.SetToolTip(e, text);
}

/// <summary>Two columns of sections (top-aligned) when the host is at least <c>min</c> wide, else stacked (left first). Re-arranges only when that flips.</summary>
internal sealed partial class TwoUp : Grid
{
    public readonly StackPanel Left = new() { Spacing = 20 }, Right = new() { Spacing = 20 };
    readonly double min;
    bool? wide;
    bool lastWide;

    public TwoUp(double minWidth, double width, double spacing = 32)
    {
        min = minWidth;
        ColumnSpacing = spacing; RowSpacing = 20;
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(Left); Children.Add(Right);
        Left.VerticalAlignment = Right.VerticalAlignment = VerticalAlignment.Top;
        Arrange(width >= min);
        SizeChanged += (_, e) => Arrange(e.NewSize.Width >= min);
    }

    void Arrange(bool w)
    {
        lastWide = w;
        if (wide == w) return;
        wide = w;
        bool alone = Right.Children.Count == 0;   // nothing on the right: the left column takes the whole width
        SetRow(Left, 0); SetColumn(Left, 0); SetColumnSpan(Left, w && !alone ? 1 : 2);
        SetRow(Right, w ? 0 : 1); SetColumn(Right, w ? 1 : 0); SetColumnSpan(Right, w ? 1 : 2);
        Right.Visibility = Right.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Hide the empty right column after the cards are added.</summary>
    public TwoUp Done()
    {
        Right.Visibility = Right.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        wide = null; Arrange(lastWide);
        return this;
    }
}

/// <summary>Peak timeline (§5.6.4): axis, ticks and one rounded bar per peak, coloured by kind. Redrawn on size change only.</summary>
internal sealed partial class PeakTimeline : Canvas
{
    readonly IReadOnlyList<Peak> peaks;
    readonly DateTime from;
    readonly int days;

    public PeakTimeline(IReadOnlyList<Peak> peaks, DateTime from, int days)
    {
        this.peaks = peaks; this.from = from; this.days = days;
        Height = 16;
        SizeChanged += (_, _) => Draw();
    }

    public static string KindKey(PeakKind k) => k switch { PeakKind.Cpu => "FuseboxAccent", PeakKind.Gpu => "FuseboxGood", PeakKind.Temp => "FuseboxWarn", PeakKind.Mem => "FuseboxDanger", _ => "FuseboxSecondary" };
    public static Brush KindBrush(PeakKind k) => Ui.Blocks ? Ui.OnRow() : Ui.B(KindKey(k));

    void Draw()
    {
        Children.Clear();
        double w = ActualWidth;
        if (w <= 0) return;
        var line = Ui.OnRow(true);
        Children.Add(new Rectangle { Width = w, Height = 1, Fill = line, Opacity = 0.4, Margin = new Thickness(0, 8, 0, 0) });
        int ticks = days == 1 ? 4 : days;
        for (int i = 0; i <= ticks; i++)
            Children.Add(new Rectangle { Width = 1, Height = 8, Fill = line, Opacity = 0.4, Margin = new Thickness(Math.Min(w - 1, w * i / ticks), 4, 0, 0) });
        double total = days * 86400;
        foreach (var p in peaks)
        {
            double x = Math.Clamp((p.Start - from).TotalSeconds / total, 0, 1) * w;
            double len = Math.Max((p.End - p.Start).TotalSeconds / total * w, 5);
            Children.Add(new Rectangle { Width = Math.Min(len, Math.Max(w - x, 5)), Height = 10, RadiusX = 3, RadiusY = 3, Fill = KindBrush(p.Kind), Opacity = 0.85, Margin = new Thickness(Math.Min(x, w - 5), 3, 0, 0) });
        }
    }
}
