using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Fusebox.App.Pages;

/// <summary>Flow layout for the chips of an expanded device row: 4 DIPs between items, wraps when the next one would overflow.</summary>
public sealed partial class NetWrap : Panel
{
    public double Gap { get; set; } = 4;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 10_000 : available.Width, x = 0, y = 0, rowH = 0, maxX = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(w, double.PositiveInfinity));
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > w) { y += rowH + Gap; x = 0; rowH = 0; }
            x += s.Width + Gap; rowH = Math.Max(rowH, s.Height); maxX = Math.Max(maxX, x - Gap);
        }
        return new Size(maxX, y + rowH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, rowH = 0;
        foreach (var c in Children)
        {
            var s = c.DesiredSize;
            if (x > 0 && x + s.Width > final.Width) { y += rowH + Gap; x = 0; rowH = 0; }
            c.Arrange(new Rect(x, y, s.Width, s.Height));
            x += s.Width + Gap; rowH = Math.Max(rowH, s.Height);
        }
        return final;
    }
}

/// <summary>Live reply-time graph: main line 1.5 DIPs with 15 % fill, second line 1 DIP at 50 %. Right-aligned, grows from the right until 60 points exist. Redraws only when set.</summary>
public sealed partial class NetGraph : Grid
{
    const int Points = 60;
    readonly Polygon fill = new();
    readonly Polyline main = new() { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly Polyline second = new() { StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round };
    IReadOnlyList<double> a = [], b = [];
    double top = 5;

    public NetGraph()
    {
        fill.Fill = ThemeService.Brush("FuseboxGraphFill");
        main.Stroke = ThemeService.Brush("FuseboxGraph");
        second.Stroke = ThemeService.Brush("FuseboxGraphSecond");
        Children.Add(fill); Children.Add(second); Children.Add(main);
        SizeChanged += (_, _) => Draw();
    }

    public void Set(IReadOnlyList<double> mainValues, IReadOnlyList<double> secondValues, double yTop)
    {
        a = mainValues; b = secondValues; top = yTop;
        Draw();
    }

    PointCollection Line(IReadOnlyList<double> v, double w, double h)
    {
        var pts = new PointCollection();
        if (v.Count < 2) return pts;
        double span = w * (v.Count - 1) / (Points - 1), x0 = w - span;   // right-aligned
        for (int i = 0; i < v.Count; i++)
            pts.Add(new Point(x0 + span * i / (v.Count - 1), h - 1 - Math.Clamp(v[i] / top, 0, 1) * (h - 2)));
        return pts;
    }

    void Draw()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var m = Line(a, w, h);
        main.Points = m;
        second.Points = Line(b, w, h);
        var f = new PointCollection();
        if (m.Count > 1)
        {
            foreach (var p in m) f.Add(p);
            f.Add(new Point(m[^1].X, h)); f.Add(new Point(m[0].X, h));
        }
        fill.Points = f;
    }
}
