using Fusebox.App.Controls;
using Fusebox.Core.Docked;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>Chips that wrap onto the next line (spacing 6/6), like the Mac's NetFlow.</summary>
public sealed partial class WrapFlow : Panel
{
    public double Gap { get; set; } = 6;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, lineH = 0, maxW = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(available.Width, double.PositiveInfinity));
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > available.Width) { y += lineH + Gap; x = 0; lineH = 0; }
            x += s.Width + Gap; lineH = Math.Max(lineH, s.Height); maxW = Math.Max(maxW, x - Gap);
        }
        return new Size(double.IsInfinity(available.Width) ? maxW : Math.Min(maxW, available.Width), y + lineH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, lineH = 0;
        foreach (var c in Children)
        {
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > final.Width) { y += lineH + Gap; x = 0; lineH = 0; }
            c.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width + Gap; lineH = Math.Max(lineH, s.Height);
        }
        return final;
    }
}

/// <summary>Builds the Docked page's elements from the view records (spec §7). Colours only through theme brush keys, numbers with Look.Number.</summary>
static class DockedUi
{
    static Brush B(string key) => ThemeService.Brush(key);
    static readonly FontFamily Icons = new("Segoe Fluent Icons");

    public static TextBlock Glyph(string glyph, double size, string brush = "FuseboxSecondary") =>
        new() { Text = glyph, FontFamily = Icons, FontSize = ThemeService.Current.Size(size), Foreground = B(brush), VerticalAlignment = VerticalAlignment.Center, IsTextScaleFactorEnabled = false };

    public static TextBlock Label(string text, double size, string brush = "FuseboxOnRow", bool semibold = false, bool wrap = false)
    {
        var t = new TextBlock { Text = text, Foreground = B(brush), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis };
        Look.Label(t, size);
        if (semibold) t.FontWeight = FontWeights.SemiBold;
        return t;
    }

    public static TextBlock Number(string text, double size, string brush = "FuseboxOnRow")
    {
        var t = new TextBlock { Text = text, Foreground = B(brush), TextWrapping = TextWrapping.NoWrap };
        Look.Number(t, size);
        return t;
    }

    // ----- right-hand number (spec §7.8) -----
    public static FrameworkElement Right(RightValue r, double size)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right };
        string main = r.Slow ? "FuseboxWarn" : "FuseboxOnRow";
        if (r.Glyph != null) panel.Children.Add(Glyph(r.Glyph, size * 0.7, r.Slow ? "FuseboxWarn" : "FuseboxOnRowSecondary"));
        double small = Math.Max(size * 0.5, 9);
        if (r.Prefix != null) panel.Children.Add(Bottom(Label(r.Prefix, small, "FuseboxOnRowSecondary")));
        panel.Children.Add(Number(r.Big, size, main));
        if (r.Slow && r.Glyph == null) panel.Children.Add(Glyph("", size * 0.55, "FuseboxWarn"));   // arrow down
        if (r.Suffix != null) panel.Children.Add(Bottom(Label(r.Suffix, small, "FuseboxOnRowSecondary")));
        return panel;
    }

    static TextBlock Bottom(TextBlock t) { t.VerticalAlignment = VerticalAlignment.Bottom; t.Margin = new Thickness(0, 0, 0, 1); return t; }

    // ----- connectors (spec §7.7): 16 pt columns, 1.5 pt lines at 40 % -----
    public static UIElement Connector(Conn kind)
    {
        var cell = new Grid { Width = 16 };
        Rectangle Line(double w, double h) => new() { Width = w, Height = h, Fill = B("FuseboxOnRowSecondary"), Opacity = 0.4 };
        switch (kind)
        {
            case Conn.Pass:
            case Conn.Tee:
                var v = Line(1.5, double.NaN); v.HorizontalAlignment = HorizontalAlignment.Left; v.VerticalAlignment = VerticalAlignment.Stretch; v.Margin = new Thickness(6, 0, 0, 0);
                cell.Children.Add(v);
                if (kind == Conn.Tee)
                {
                    var h = Line(12, 1.5); h.HorizontalAlignment = HorizontalAlignment.Left; h.VerticalAlignment = VerticalAlignment.Top; h.Margin = new Thickness(6, 14.25, 0, 0);
                    cell.Children.Add(h);
                }
                break;
            case Conn.Elbow:
                var fig = new PathFigure { StartPoint = new Point(6.75, 0), IsClosed = false };
                fig.Segments.Add(new LineSegment { Point = new Point(6.75, 11) });
                fig.Segments.Add(new QuadraticBezierSegment { Point1 = new Point(6.75, 15), Point2 = new Point(10.75, 15) });
                fig.Segments.Add(new LineSegment { Point = new Point(18, 15) });
                var geo = new PathGeometry(); geo.Figures.Add(fig);
                cell.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geo, Stroke = B("FuseboxOnRowSecondary"), StrokeThickness = 1.5, Opacity = 0.4,
                    VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Height = 16, Width = 18,
                });
                break;
        }
        return cell;
    }

    static StackPanel Connectors(IEnumerable<Conn> cols)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in cols) sp.Children.Add(Connector(c));
        return sp;
    }

    /// <summary>Continuation of the tree lines below a row (for inline details): Tee/Pass keep their line, Elbow/None leave a gap.</summary>
    public static IEnumerable<Conn> Below(IReadOnlyList<Conn> cols, bool hasKids) =>
        cols.Select(c => c is Conn.Tee or Conn.Pass ? Conn.Pass : Conn.None).Concat(hasKids ? [Conn.Pass] : []);

    // ----- a tree row -----
    public static UIElement Row(TreeRow r, bool selected, bool hasKids, Style buttonStyle, Action<TreeRow> click, UIElement? inlineDetail, Dictionary<string, Button> buttons)
    {
        var theme = ThemeService.Current;
        var content = new StackPanel { Spacing = 6 };
        var top = new Grid { ColumnSpacing = 7 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = Glyph(r.Glyph, 11, r.Dim ? "FuseboxOnRowSecondary" : "FuseboxOnRowSecondary");
        icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 4, 0, 0); icon.HorizontalAlignment = HorizontalAlignment.Center;
        top.Children.Add(icon);

        var text = new StackPanel { Spacing = 1 };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        titleLine.Children.Add(Label(r.Title, r.Port ? 13 : 12, r.Dim ? "FuseboxOnRowSecondary" : "FuseboxOnRow", semibold: true));
        if (r.Info != null)
        {
            var info = Glyph("", 10);
            ToolTipService.SetToolTip(info, r.Info);
            titleLine.Children.Add(info);
        }
        text.Children.Add(titleLine);
        if (r.Facts.Count > 0) text.Children.Add(Label(string.Join(" · ", r.Facts), 10, "FuseboxOnRowSecondary"));
        Grid.SetColumn(text, 1); top.Children.Add(text);
        if (r.Right != null)
        {
            var right = Right(r.Right, r.Port ? 16 : 14);
            Grid.SetColumn(right, 2); top.Children.Add(right);
        }
        content.Children.Add(top);
        if (r.Warning != null)
        {
            var w = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(23, 0, 0, 0) };
            w.Children.Add(Glyph("", 10, "FuseboxWarn"));
            w.Children.Add(new Border { MaxWidth = 460, Child = Label(r.Warning, 10, "FuseboxWarn", wrap: true) });
            content.Children.Add(w);
        }

        var card = new Border { Child = content, Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(7), Background = selected ? B("FuseboxAccentSoft") : null };
        UIElement body = card;
        if (r.Detail != null)
        {
            var btn = new Button { Content = card, Style = buttonStyle, CornerRadius = new CornerRadius(7) };
            btn.Click += (_, _) => click(r);
            ToolTipService.SetToolTip(btn, null);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, r.Title + (r.Facts.Count > 0 ? ", " + string.Join(", ", r.Facts) : ""));
            buttons[r.Id] = btn;
            body = btn;
        }

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(Connectors(r.Cols));
        Grid.SetColumn((FrameworkElement)body, 1); line.Children.Add(body);
        if (inlineDetail == null) return line;

        var all = new StackPanel();
        all.Children.Add(line);
        var under = new Grid();
        under.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        under.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        under.Children.Add(Connectors(Below(r.Cols, hasKids)));
        var pad = new Border { Padding = new Thickness(29, 0, 0, 8), Child = inlineDetail };
        Grid.SetColumn(pad, 1); under.Children.Add(pad);
        all.Children.Add(under);
        return all;
    }

    // ----- detail panel (spec §7.10) -----
    public static UIElement DetailPanel(Detail d)
    {
        var sp = new StackPanel { Spacing = 8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        head.Children.Add(Glyph(d.Glyph, 12));
        head.Children.Add(Label(d.Title, 14, semibold: true, wrap: true));
        sp.Children.Add(head);
        if (d.Big != null) sp.Children.Add(Right(d.Big, 22));
        if (d.Notes.Count > 0)
        {
            var notes = new StackPanel { Spacing = 4 };
            foreach (var n in d.Notes) notes.Children.Add(NoteBox(n));
            sp.Children.Add(notes);
        }
        foreach (var g in d.Groups)
        {
            var sec = new StackPanel { Spacing = 4 };
            var h = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            h.Children.Add(Glyph(g.Glyph, 10));
            h.Children.Add(Label(g.Title.ToUpperInvariant(), 10, "FuseboxOnRowSecondary"));
            sec.Children.Add(h);
            foreach (var f in g.Facts) sec.Children.Add(Fact(f));
            sp.Children.Add(sec);
        }
        if (d.Raw.Count > 0) sp.Children.Add(Raw(d.Raw));
        return sp;
    }

    static UIElement Fact(Fact f)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(Label(f.Key, 11, "FuseboxOnRowSecondary", wrap: true));
        var v = Label(f.Value, 11, wrap: true);
        v.IsTextSelectionEnabled = true;
        Grid.SetColumn(v, 1); g.Children.Add(v);
        return g;
    }

    static UIElement NoteBox(Note n)
    {
        var (glyph, brush) = n.Level switch { NoteLevel.Warn => ("", "FuseboxWarn"), NoteLevel.Good => ("", "FuseboxGood"), _ => ("", "FuseboxAccent") };
        var g = new Grid();
        g.Children.Add(new Border { Background = B(brush), Opacity = 0.14, CornerRadius = new CornerRadius(7) });
        var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(8, 5, 8, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Glyph(glyph, 11, brush); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 2, 0, 0);
        row.Children.Add(icon);
        var text = Label(n.Text, 11, wrap: true);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        g.Children.Add(row);
        return g;
    }

    static UIElement Raw(IReadOnlyList<(string Key, string Value)> raw)
    {
        var lines = new StackPanel { Spacing = 1, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (k, v) in raw)
        {
            var t = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = ThemeService.Current.Size(10), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = k + "  ", Foreground = B("FuseboxOnRowSecondary") });
            t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = v, Foreground = B("FuseboxOnRow") });
            lines.Children.Add(t);
        }
        return new Expander { Header = Label(L("All values"), 11, "FuseboxOnRowSecondary"), Content = lines, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    // ----- header card (spec §7.3) -----
    public static UIElement HeaderCard(Header h)
    {
        var card = new Border();
        ThemedRow.Apply(card, 0);
        var stack = new StackPanel { Spacing = 10 };
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var big = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var bigText = Number(h.Big, 34);
        bigText.TextWrapping = TextWrapping.Wrap;
        big.Children.Add(bigText);
        if (h.Unit != null) big.Children.Add(Bottom(Label(h.Unit, 34 * 0.42, "FuseboxOnRowSecondary")));
        left.Children.Add(big);
        if (h.Caption != null) left.Children.Add(Label(h.Caption, 10, "FuseboxOnRowSecondary"));
        row.Children.Add(left);

        if (h.Percent != null)
        {
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var pct = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
            pct.Children.Add(Number(h.Percent, 24));
            pct.Children.Add(Bottom(Label("%", 24 * 0.42, "FuseboxOnRowSecondary")));
            right.Children.Add(pct);
            if (h.BatteryText != null) { var t = Label(h.BatteryText, 10, "FuseboxOnRowSecondary"); t.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(t); }
            Grid.SetColumn(right, 1); row.Children.Add(right);
        }
        stack.Children.Add(row);

        if (h.Chips.Count > 0)
        {
            var flow = new WrapFlow();
            foreach (var c in h.Chips) flow.Children.Add(Chip(c));
            stack.Children.Add(flow);
        }
        if (h.Footnote != null) stack.Children.Add(Label(h.Footnote, 10, "FuseboxOnRowSecondary", wrap: true));
        card.Child = stack;
        return card;
    }

    static UIElement Chip(Chip c)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sp.Children.Add(Glyph(c.Glyph, 9));
        sp.Children.Add(Number(c.Value, 11));
        sp.Children.Add(Label(c.Label, 10, "FuseboxOnRowSecondary"));
        var b = new Border { Child = sp, Background = B("FuseboxHover"), CornerRadius = new CornerRadius(100), Padding = new Thickness(7, 3, 7, 3) };
        if (c.Tip != null) ToolTipService.SetToolTip(b, c.Tip);
        return b;
    }

    /// <summary>A group card in the theme's row style (cards, blocks, glass …).</summary>
    public static Border GroupCard(UIElement child, int index)
    {
        var b = new Border { Child = child };
        ThemedRow.Apply(b, index);
        return b;
    }
}
