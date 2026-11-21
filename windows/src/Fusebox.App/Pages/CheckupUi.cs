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
/// The Checkup building blocks (§5.6.0 / §5.B.0) built in code: theme text colours, tones, CardTitle, Headline, Pill, Capped, mini bars.
/// Everything reads the theme at build time; the page rebuilds on ThemeChanged (cached data, so it is cheap).
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

    public static FontIcon Icon(string glyph, double pt, Brush? color = null) =>
        new() { Glyph = glyph, FontSize = T.Size(pt), Foreground = color ?? Tone(0), VerticalAlignment = VerticalAlignment.Center };

    // ---- structure ----

    public static ThemedRow Card(int index, UIElement child) => new() { Index = index, Child = child, HorizontalAlignment = HorizontalAlignment.Stretch };

    public static StackPanel Stack(double spacing = 5, params UIElement[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    /// <summary>Icon (16 wide), title (semibold 12) and an optional trailing element pushed to the right.</summary>
    public static Grid CardTitle(string glyph, string title, int tone = 0, UIElement? trailing = null)
    {
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = Icon(glyph, 11, Tone(tone));
        icon.Width = 16;
        g.Children.Add(icon);
        var t = Label(title, 12, bold: true, wrap: false);
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        if (trailing != null) { Grid.SetColumn((FrameworkElement)trailing, 2); g.Children.Add(trailing); }
        return g;
    }

    public static StackPanel Headline(string big, string? small = null, int tone = 0)
    {
        var s = new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 0, 2) };
        var b = Num(big, 22, color: Tone(tone, false));
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

    public static UIElement Sub(string text) { var t = Label(text, 10, secondary: true); t.Margin = new Thickness(0, 3, 0, 0); return t; }

    public static Rectangle Divider() => new() { Height = 1, Fill = Blocks ? new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.3 } : B("FuseboxLine"), Margin = new Thickness(0, 3, 0, 3) };

    // ---- controls ----

    /// <summary>Small outlined capsule button; <paramref name="text"/> and <paramref name="tooltip"/> are already translated (call sites write L("…") so the translation check sees them). (1 px stroke at 60 % of its colour, no fill).</summary>
    public static Button Pill(string text, Brush? color, Action click, string? tooltip = null)
    {
        color ??= OnRow();
        var label = new TextBlock { Text = text, Foreground = color, FontSize = T.Size(11), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };
        var b = new Button
        {
            Content = label, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(ColorOf(color)) { Opacity = 0.6 }, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999),
            Padding = new Thickness(9, 3, 9, 3), MinHeight = 0, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => click();
        if (tooltip != null) ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    /// <summary>Plain text button (accent, semibold 11): "Show all (N)".</summary>
    public static Button TextButton(string text, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, Foreground = Tone(0), FontSize = T.Size(11), FontWeight = FontWeights.SemiBold },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0, 1, 0, 0), MinHeight = 0, MinWidth = 0,
        };
        b.Click += (_, _) => click();
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

    /// <summary>"can go" / "check first" / "keep" / "?" tag (9 pt semibold, capsule stroke).</summary>
    public static Border Tag(string text, Brush color) => new()
    {
        Child = new TextBlock { Text = text, Foreground = color, FontSize = T.Size(9), FontWeight = FontWeights.SemiBold },
        BorderBrush = new SolidColorBrush(ColorOf(color)) { Opacity = 0.6 }, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999),
        Padding = new Thickness(5, 1, 5, 1), VerticalAlignment = VerticalAlignment.Center,
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

    public static Ellipse Dot(bool on)
    {
        var d = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        if (on) d.Fill = Tone(3); else { d.Fill = OnRow(true); d.Opacity = 0.35; }
        return d;
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
            Button? btn = null;
            btn = TextButton(L("Show all (%lld)", rows.Count), () =>
            {
                all = !all;
                btn!.Content = new TextBlock { Text = all ? L("Show less") : L("Show all (%lld)", rows.Count), Foreground = Tone(0), FontSize = T.Size(11), FontWeight = FontWeights.SemiBold };
                Apply();
            });
            s.Children.Add(btn);
        }
        Apply();
        return s;
    }

    public static void Tip(FrameworkElement e, string text) => ToolTipService.SetToolTip(e, text);
}

/// <summary>Two cards side by side when the host is at least <c>min</c> wide, else stacked (left first). Re-arranges only when that flips.</summary>
internal sealed partial class TwoUp : Grid
{
    public readonly StackPanel Left = new() { Spacing = 10 }, Right = new() { Spacing = 10 };
    readonly double min;
    bool? wide;
    bool lastWide;

    public TwoUp(double minWidth, double width, double spacing = 12)
    {
        min = minWidth;
        ColumnSpacing = spacing; RowSpacing = 10;
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
