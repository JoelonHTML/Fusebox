using Fusebox.Core.Look;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;

namespace Fusebox.App.Controls;

/// <summary>
/// Theme typography (Mac theme.number / theme.label): theme font, size × textScale, numbers in the theme weight with tabular digits,
/// labels Medium. Re-applied automatically on ThemeChanged.
/// XAML: <c>&lt;TextBlock c:Look.Number="30" Foreground="{ThemeResource FuseboxText}" /&gt;</c> · code: <c>Look.Number(tb, 30)</c>.
/// </summary>
public sealed partial class Look
{
    public static readonly DependencyProperty NumberProperty =
        DependencyProperty.RegisterAttached("Number", typeof(double), typeof(Look), new PropertyMetadata(0.0, (d, _) => Track(d)));
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.RegisterAttached("Label", typeof(double), typeof(Look), new PropertyMetadata(0.0, (d, _) => Track(d)));

    public static double GetNumber(DependencyObject o) => (double)o.GetValue(NumberProperty);
    public static void SetNumber(DependencyObject o, double pt) => o.SetValue(NumberProperty, pt);
    public static double GetLabel(DependencyObject o) => (double)o.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject o, double pt) => o.SetValue(LabelProperty, pt);

    /// <summary>Number text at <paramref name="pt"/> (× textScale), theme weight, tabular digits.</summary>
    public static void Number(TextBlock t, double pt) => SetNumber(t, pt);
    /// <summary>Label text at <paramref name="pt"/> (× textScale), Medium.</summary>
    public static void Label(TextBlock t, double pt) => SetLabel(t, pt);

    static readonly List<WeakReference<TextBlock>> tracked = [];
    static bool hooked;

    static void Track(DependencyObject d)
    {
        if (d is not TextBlock t) return;
        Style(t);
        if (!hooked) { hooked = true; ThemeService.ThemeChanged += Restyle; }
        if (tracked.Count % 256 == 255) tracked.RemoveAll(w => !w.TryGetTarget(out _));
        tracked.Add(new(t));
    }

    static void Restyle()
    {
        tracked.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in tracked) if (w.TryGetTarget(out var t)) Style(t);
    }

    static void Style(TextBlock t)
    {
        var theme = ThemeService.Current;
        double n = GetNumber(t), l = GetLabel(t);
        if (n <= 0 && l <= 0) return;
        t.FontFamily = ThemeService.Family(theme.Face);
        if (n > 0)
        {
            t.FontSize = theme.Size(n);
            t.FontWeight = ThemeService.NumberWeight;
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        }
        else
        {
            t.FontSize = theme.Size(l);
            t.FontWeight = FontWeights.Medium;
        }
    }
}

/// <summary>
/// A row/card in the theme's row style (§10.5, Mac .themedRow(theme, index)): lines (bottom rule), plain, cards (12), blocks (10, color by
/// Index), glass (14, gradient fill + rim; no blur). Follows theme changes while loaded.
/// XAML: <c>&lt;c:ThemedRow Index="1"&gt;…&lt;/c:ThemedRow&gt;</c>. Text on a row: Foreground FuseboxOnRow / FuseboxOnRowSecondary.
/// </summary>
[ContentProperty(Name = nameof(Child))]
public sealed partial class ThemedRow : UserControl
{
    readonly Border border = new();
    int index;

    public ThemedRow()
    {
        Content = border;
        Loaded += (_, _) => { ThemeService.ThemeChanged += Restyle; Restyle(); };
        Unloaded += (_, _) => ThemeService.ThemeChanged -= Restyle;
        Restyle();
    }

    public UIElement? Child { get => border.Child; set => border.Child = value; }

    /// <summary>Row index: picks the block color in Color blocks (index % 5).</summary>
    public int Index { get => index; set { index = value; Restyle(); } }

    void Restyle() => Apply(border, index);

    /// <summary>Styles a Border you own as a themed row (call again on ThemeChanged).</summary>
    public static void Apply(Border b, int index = 0)
    {
        var t = ThemeService.Current;
        var s = RowSpec.For(t.RowStyle);
        b.Padding = new Thickness(s.PadH, s.PadV, s.PadH, s.PadV);
        b.CornerRadius = new CornerRadius(s.Radius);
        switch (t.RowStyle)
        {
            case RowStyle.Lines:
                b.Background = null; b.BorderBrush = ThemeService.Brush("FuseboxLine"); b.BorderThickness = new Thickness(0, 0, 0, 1); break;
            case RowStyle.Plain:
                b.Background = null; b.BorderBrush = null; b.BorderThickness = new Thickness(0); break;
            case RowStyle.Cards:
                b.Background = ThemeService.Brush("FuseboxCardFill"); b.BorderBrush = ThemeService.Brush("FuseboxLine"); b.BorderThickness = new Thickness(1); break;
            case RowStyle.Blocks:
                b.Background = ThemeService.Brush($"FuseboxBlock{(index % 5 + 5) % 5}"); b.BorderBrush = null; b.BorderThickness = new Thickness(0); break;
            default:
                b.Background = ThemeService.Brush("FuseboxGlassFill"); b.BorderBrush = ThemeService.Brush("FuseboxGlassRim"); b.BorderThickness = new Thickness(1); break;
        }
    }
}
