using Fusebox.Core;
using Fusebox.Core.Look;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.ViewManagement;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Live previews in the Fusebox theme (§9.3): theme tiles, the fake Live page, option previews, table previews, the number demo.
/// Colors, fonts and row shapes are explicit from a resolved <see cref="Theme"/> (not the app brushes), so a tile can show a theme that
/// is not the current one. Callers rebuild a preview on <see cref="ThemeService.ThemeChanged"/> / pref changes (Kit.WhileTheme).
/// Text here is sample data, not translated.
/// </summary>
internal static class Previews
{
    static readonly UISettings ui = new();

    static Argb SystemAccent()
    {
        try { var c = ui.GetColorValue(UIColorType.Accent); return new(255, c.R, c.G, c.B); } catch { return new(255, 0, 120, 212); }
    }

    /// <summary>A theme resolved like the real one (accent, font, light/dark mode, Low impact) at the given text scale (−1 = the pref).</summary>
    public static Theme Resolve(string id, double scale = -1) =>
        Themes.Resolve(id, Prefs.Str("accent"), scale < 0 ? Prefs.Double("textScale") : scale, Prefs.Str("font"), ThemeService.Target, LowImpact.On, SystemAccent());

    public static Theme Current() => Resolve(Prefs.Str("theme"));

    public static SolidColorBrush B(Argb c) => new(ThemeService.C(c));

    /// <summary>Window background of a theme; Liquid Glass has none (Mica): a neutral grey for the scheme.</summary>
    public static Argb Bg(Theme t) => t.Bg ?? (t.IsDark ? Argb.White(0.16) : Argb.White(0.96));

    static Windows.UI.Text.FontWeight NumberWeight(Theme t) => t.Weight switch { Core.Look.NumberWeight.Bold => FontWeights.Bold, Core.Look.NumberWeight.Semibold => FontWeights.SemiBold, _ => FontWeights.Normal };

    /// <summary>Text in the theme's font: a number (theme weight, tabular digits) or a label (Medium).</summary>
    public static TextBlock T(string s, Theme t, double pt, Argb color, bool number)
    {
        var tb = new TextBlock
        {
            Text = s, FontFamily = ThemeService.Family(t.Face), FontSize = t.Size(pt), Foreground = B(color), TextWrapping = TextWrapping.NoWrap,
            FontWeight = number ? NumberWeight(t) : FontWeights.Medium, VerticalAlignment = VerticalAlignment.Bottom,
        };
        if (number) Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    /// <summary>The theme's row container (§10.5) around <paramref name="content"/>.</summary>
    public static Border Row(Theme t, int index, UIElement content)
    {
        var s = RowSpec.For(t.RowStyle);
        var b = new Border { Child = content, Padding = new Thickness(s.PadH, s.PadV, s.PadH, s.PadV), CornerRadius = new CornerRadius(s.Radius) };
        switch (t.RowStyle)
        {
            case RowStyle.Lines: b.BorderBrush = B(t.Line); b.BorderThickness = new Thickness(0, 0, 0, 1); break;
            case RowStyle.Plain: break;
            case RowStyle.Cards: b.Background = B(t.Card); b.BorderBrush = B(t.Line); b.BorderThickness = new Thickness(1); break;
            case RowStyle.Blocks: b.Background = B(t.Block(index)); break;
            default:
                var g = RowSpec.Glass(t.Scheme);
                b.Background = Gradient(g.FillTop, g.FillBottom); b.BorderBrush = Gradient(g.RimTop, g.RimBottom); b.BorderThickness = new Thickness(1);
                break;
        }
        return b;
    }

    static LinearGradientBrush Gradient(Argb top, Argb bottom)
    {
        var g = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        g.GradientStops.Add(new GradientStop { Color = ThemeService.C(top), Offset = 0 });
        g.GradientStops.Add(new GradientStop { Color = ThemeService.C(bottom), Offset = 1 });
        return g;
    }

    /// <summary>One metric line: label · side · … · value · unit, bottoms aligned.</summary>
    public static Grid Line(Theme t, string label, string side, string value, string unit, double labelPt, double sidePt, double valuePt, string? glyph = null, UIElement? extra = null)
    {
        var g = new Grid { ColumnSpacing = 5 };
        for (int i = 0; i < 5; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        void Put(UIElement e, int col) { Grid.SetColumn((FrameworkElement)e, col); g.Children.Add(e); }
        var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Bottom };
        inner.Children.Add(T(label, t, labelPt, t.LabelColor, false));
        if (glyph != null) inner.Children.Add(new FontIcon { Glyph = glyph, FontSize = labelPt, Foreground = B(t.OnRow(true)), VerticalAlignment = VerticalAlignment.Bottom });
        if (side.Length > 0) inner.Children.Add(T(side, t, sidePt, t.OnRow(true), true));
        if (extra != null) inner.Children.Add(extra);
        Put(inner, 0);
        var v = T(value, t, valuePt, t.OnRow(), true);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        Put(v, 3);
        if (unit.Length > 0) Put(T(unit, t, sidePt, t.OnRow(true), true), 4);
        return g;
    }

    /// <summary>The theme's window background around content (preview box).</summary>
    public static Border Box(Theme t, UIElement content, double radius, double padding) =>
        new() { Child = content, Background = B(Bg(t)), CornerRadius = new CornerRadius(radius), Padding = new Thickness(padding) };

    /// <summary>Theme tile content (§11.7): a mini Live window, two rows, in that theme.</summary>
    public static UIElement ThemeTile(Theme t)
    {
        var rows = new StackPanel { Spacing = t.RowStyle is RowStyle.Lines or RowStyle.Plain ? 0 : 4 };
        rows.Children.Add(Row(t, 0, Line(t, "CPU", "47°", "17%", "", 9, 9, 14)));
        rows.Children.Add(Row(t, 1, Line(t, "RAM", "", "11.8", "GB", 9, 9, 14)));
        var box = Box(t, rows, 8, 8);
        box.MinHeight = 86;
        return box;
    }

    /// <summary>One fake Live row under a toggle (Mac OptionPreview): label, optional icon, side text, optional extra, value, unit.</summary>
    public static UIElement OptionPreview(string label, string? glyph, string side, string value, string unit, UIElement? extra = null)
    {
        var t = Current();
        return Box(t, Row(t, 0, Line(t, label, side, value, unit, 13, 13, 22, glyph, extra)), 10, 8);
    }

    /// <summary>Fake table header + one row (Mac TablePreview): columns (title, sample value), the sorted one marked ▾.</summary>
    public static UIElement TablePreview(string name, (string Title, string Value, bool Sorted)[] columns, bool nameSorted = false)
    {
        var t = Current();
        var g = new Grid { ColumnSpacing = 10, RowSpacing = 4 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < columns.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(TextBlock tb, int col, int row)
        {
            tb.HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            tb.TextTrimming = TextTrimming.None;
            Grid.SetColumn(tb, col); Grid.SetRow(tb, row); g.Children.Add(tb);
        }
        Put(T(L("Name") + (nameSorted ? " ▾" : ""), t, 10, nameSorted ? t.OnRow() : t.OnRow(true), false), 0, 0);
        Put(T(name, t, 12, t.OnRow(), false), 0, 1);
        for (int i = 0; i < columns.Length; i++)
        {
            Put(T(columns[i].Title + (columns[i].Sorted ? " ▾" : ""), t, 10, columns[i].Sorted ? t.OnRow() : t.OnRow(true), false), i + 1, 0);
            Put(T(columns[i].Value, t, 12, t.OnRow(), true), i + 1, 1);
        }
        return Box(t, Row(t, 0, g), 10, 8);
    }

    // ---- sample data (§9.6 PreviewData) ----

    public static double[] Series(Metric m, int n = 24) => Enumerable.Range(0, n).Select(i => m switch
    {
        Metric.Cpu => 22 + 14 * Math.Sin(i / 2.3) + 6 * Math.Sin(1.7 * i),
        Metric.Gpu => 8 + 5 * Math.Sin(i / 3.0),
        Metric.Ram => 72 + 2 * Math.Sin(i / 4.0),
        Metric.Net => 1.05e6 + 5e5 * Math.Sin(i / 1.8),
        Metric.Ssd => 40,
        Metric.Io => 3.2e6 + 1.5e6 * Math.Sin(i / 2.0),
        _ => 80 - i / 4.0,
    }).ToArray();

    /// <summary>The fake "now" of a metric: value, unit, and the max for its graph scale.</summary>
    public static (string Value, string Unit) Now(Metric m, bool absolute)
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        double gb = App.Live.Last is { RamTotal: > 0 } s ? s.RamTotal / 1e9 : 16;
        switch (m)
        {
            case Metric.Cpu: return absolute ? ((0.17 * cores).ToString("0.0", Strings.Culture), $"/{cores}") : ("17%", "");
            case Metric.Gpu: return ("9%", "");
            case Metric.Ram: return absolute ? ((0.74 * gb).ToString("0.0", Strings.Culture), "GB") : ("74%", "");
            case Metric.Net: { var (a, b, u) = Fmt.RatePair(900_000, 150_000, Prefs.Bool("netBits")); return ($"↓{a} ↑{b}", u); }
            case Metric.Ssd: return absolute ? ("300", "GB") : ("40%", "");
            case Metric.Io: return ("3.2", "MB/s");
            default: return ("80%", "");
        }
    }

    /// <summary>A small line graph (explicit stroke), values scaled to the largest, drawn once.</summary>
    public static Canvas Spark(double[] v, double w, double h, Argb stroke, double thickness = 1.5, double? max = null)
    {
        var c = new Canvas { Width = w, Height = h };
        var pts = new PointCollection();
        double top = max ?? Math.Max(v.Max(), 1e-9), lo = max != null ? 0 : v.Min();
        for (int i = 0; i < v.Length; i++)
            pts.Add(new Windows.Foundation.Point(w * i / (v.Length - 1), h - 1 - Math.Clamp((v[i] - lo) / Math.Max(top - lo, 1e-9), 0, 1) * (h - 2)));
        var fill = new Polygon { Fill = B(stroke.WithAlpha(0.15)) };
        foreach (var p in pts) fill.Points.Add(p);
        fill.Points.Add(new Windows.Foundation.Point(w, h)); fill.Points.Add(new Windows.Foundation.Point(0, h));
        c.Children.Add(fill);
        c.Children.Add(new Polyline { Points = pts, Stroke = B(stroke), StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round });
        return c;
    }

    /// <summary>The fake Live page (§9.6): up to 4 enabled metrics in rows of two, advice dot, time axis. Follows theme and prefs (rebuild on change).</summary>
    public static UIElement LivePreview()
    {
        var t = Current();
        bool graphs = Prefs.Str("graphs") != "never";
        var abs = Prefs.List("absolute");
        var ms = Metrics.Parse(Prefs.Str("metrics")).Take(4).ToList();
        var page = new StackPanel { Spacing = t.RowStyle is RowStyle.Lines or RowStyle.Plain ? 0 : 6 };
        if (Prefs.Bool("showAdvice"))
        {
            var advice = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
            advice.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = B(t.Good), VerticalAlignment = VerticalAlignment.Center });
            advice.Children.Add(T(L("All calm"), t, 12, t.Secondary, false));
            page.Children.Add(advice);
        }
        for (int r = 0; r < ms.Count; r += 2)
        {
            var g = new Grid { ColumnSpacing = 10, MinHeight = graphs ? 84 * t.Scale : 0 };
            g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int c = 0; c < 2 && r + c < ms.Count; c++)
            {
                var cell = Cell(t, ms[r + c], r + c, graphs, abs.Contains(Metrics.Id(ms[r + c])));
                Grid.SetColumn(cell, c);
                g.Children.Add(cell);
            }
            page.Children.Add(g);
        }
        if (ms.Count == 0) page.Children.Add(T(L("No measurements on"), t, 12, t.Secondary, false));
        if (graphs && ms.Count > 0)
        {
            int min = TimeRanges.ShortMinutes(Prefs.Long("shortMinutes"));
            var axis = new Grid();
            axis.Children.Add(T(min == 60 ? L("−1 hour") : L("−%lld min", min), t, 9, t.OnRow(true), false));
            var now = T(L("now"), t, 9, t.OnRow(true), false);
            now.HorizontalAlignment = HorizontalAlignment.Right;
            axis.Children.Add(now);
            page.Children.Add(axis);
        }
        return Box(t, page, 10, 10);
    }

    static Border Cell(Theme t, Metric m, int index, bool graphs, bool abs)
    {
        var (value, unit) = Now(m, abs);
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(Line(t, Metrics.Label(m), "", value, unit, 11, 11, graphs ? 22 : 18));
        if (graphs)
        {
            double? scale = Metrics.Scale(m) is var sc && double.IsNaN(sc) ? null : sc;
            var spark = Spark(Series(m), 150, 36, t.Graph, 1.5, scale);
            body.Children.Add(new Viewbox { Child = spark, Stretch = Stretch.Fill, Height = 36 * t.Scale, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        return Row(t, index, body);
    }

    /// <summary>The accent sample capsule (§9.5): "CPU" + a tiny graph in the theme's accent.</summary>
    public static UIElement AccentSample()
    {
        var t = Current();
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(T("CPU", t, 11, t.LabelColor, false));
        s.Children.Add(Spark(Series(Metric.Cpu, 12), 44, 14, t.Graph, 1.5, 60));
        return new Border
        {
            Child = s, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(14),
            Background = B(t.RowStyle == RowStyle.Blocks ? t.Block(0) : Bg(t)),
        };
    }

    /// <summary>Text size sample row (§11.7): "CPU 47° … 17%" in the current theme at the current scale.</summary>
    public static UIElement TextSample()
    {
        var t = Current();
        return Box(t, Row(t, 0, Line(t, "CPU", "47°", "17%", "", 13, 13, 30)), 10, 10);
    }

    // ---- number change demo (§9.5) ----

    /// <summary>Cycles 17% 23% 9% … every 1.5 s with the chosen transition, only while on screen; instant without motion.</summary>
    public static UIElement NumberDemo()
    {
        var t = Current();
        string[] values = ["17%", "23%", "9%", "41%", "34%", "12%"];
        var tb = T(values[0], t, 20, t.OnRow(), true);
        tb.HorizontalAlignment = HorizontalAlignment.Right;
        tb.RenderTransform = new TranslateTransform();
        var box = new Border { Child = tb, Width = 72, Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(8), Background = B(Bg(t)), VerticalAlignment = VerticalAlignment.Center };
        DispatcherQueueTimer? timer = null;
        int i = 0;
        box.Loaded += (_, _) =>
        {
            timer = box.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1.5);
            timer.Tick += (_, _) =>
            {
                i = (i + 1) % values.Length;
                tb.Text = values[i];
                string style = Prefs.Str("numberStyle");
                if (!ThemeService.Motion || style == "instant") return;
                if (style == "roll") Animate(tb.RenderTransform, "Y", -12, 0, 220);
                Animate(tb, "Opacity", 0, 1, style == "roll" ? 220 : 160);
            };
            timer.Start();
        };
        box.Unloaded += (_, _) => timer?.Stop();
        return box;
    }

    static void Animate(DependencyObject target, string property, double from, double to, double ms)
    {
        var a = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
    }

    // ---- tray styles (§9.11) ----

    /// <summary>The tray item as it would look (capsule, 11 pt, tabular digits): text, compact, bars, graph, icon, off.</summary>
    public static UIElement TrayPreview(string style, IReadOnlyList<Metric> metrics)
    {
        var ms = metrics.Count == 0 ? [Metric.Cpu] : metrics.Take(4).ToList();
        string Val(Metric m) => Now(m, false).Value + (Metrics.Scale(m) is var s && double.IsNaN(s) ? " " + Now(m, false).Unit : "");
        FrameworkElement content;
        switch (style)
        {
            case "bars":
                var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                foreach (var m in ms)
                {
                    double f = Math.Clamp(Series(m)[^1] / (Metrics.Scale(m) is var sc && double.IsNaN(sc) ? 100 : sc), 0.05, 1);
                    var bar = new Grid { Width = 6, Height = 14 };
                    bar.Children.Add(new Border { BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(1.5) });
                    bar.Children.Add(new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Gray), Height = 12 * f, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1.5) });
                    bars.Children.Add(bar);
                }
                content = bars;
                break;
            case "graph":
                var gs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                foreach (var m in ms) gs.Children.Add(Spark(Series(m, 12), 22, 14, new Argb(255, 128, 128, 128), 1.2, Metrics.Scale(m) is var g && double.IsNaN(g) ? null : g));
                content = gs;
                break;
            case "icon": content = new FontIcon { Glyph = "", FontSize = 14 }; break;
            case "off": content = new TextBlock { Text = "—", FontSize = 11, Opacity = 0.6 }; break;
            default:
                // "compact" would draw a small symbol per measurement (the tray icon decides how); the preview shows the one-letter form
                string s2 = string.Join("  ", ms.Select(m => style == "compact" ? $"{Metrics.Label(m)[0]} {Now(m, false).Value}" : $"{Metrics.Label(m)} {Val(m)}"));
                var tb = new TextBlock { Text = s2, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
                Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
                content = tb;
                break;
        }
        content.Height = 14;
        return new Border
        {
            Child = content, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
        };
    }
}
