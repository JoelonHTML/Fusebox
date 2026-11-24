using Fusebox.Core.Live;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;

namespace Fusebox.App.Controls;

/// <summary>A span of samples that formed a peak (indexes into the displayed series), for the highlight and for clicking.</summary>
public sealed record PeakSpan(int From, int To, object Tag);

/// <summary>
/// The Live graph (§3.5, §10.10): accent area (15 %), 1.5 px line, optional second line (50 %, 1 px), rounded polyline, right-aligned,
/// thinned when dense, scale fixed or automatic. Cheap: three Paths rebuilt only when the data or the line moved ≥ ½ device pixel.
/// Glides one step after each sample on the shared <see cref="Anim"/> clock (never when hidden / Low impact). Optional hover line with
/// a label in a band above the plot, peak highlight and peak click, and a reveal from the right edge on launch.
/// </summary>
public sealed partial class Sparkline : Grid
{
    /// <summary>Time, value and (full mode) cause for the sample at an index of the series last given to <see cref="Update"/>.</summary>
    public delegate (string Time, string Value, string? Cause) HoverText(int index);

    readonly Canvas plot = new();
    readonly Path area = new() { Fill = ThemeService.Brush("FuseboxGraphFill") };
    readonly Path line = new() { Stroke = ThemeService.Brush("FuseboxGraph"), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly Path line2 = new() { Stroke = ThemeService.Brush("FuseboxGraphSecond"), StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round };
    readonly Rectangle selected = new() { Fill = ThemeService.Brush("FuseboxAccent"), Opacity = 0.22, Visibility = Visibility.Collapsed };
    readonly Rectangle hoverLine = new() { Width = 1, Fill = ThemeService.Brush("FuseboxAccent"), Opacity = 0.55, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
    readonly TextBlock hoverText = new() { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
    readonly Run runTime = new(), runValue = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, runCause = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    IReadOnlyList<double> main = [], second = [];
    double top = 100, bottom, scale = 1, step;
    bool thinned;
    double drawnShift = double.NaN, drawnStep = double.NaN;

    // glide
    GlideKind glide;
    List<double> glideFrom = [], glideFromSecond = [];
    double glideTopFrom, glideStart;
    bool gliding;

    // reveal
    double revealP = 1;
    bool revealPending;
    int revealOrder;

    IReadOnlyList<PeakSpan> peaks = [];
    PeakSpan? selectedPeak;
    HoverText? hover;
    int hoverIndex = -1;
    bool full;

    /// <summary>Height reserved above the plot for the hover label (20 at L and up, 0 on M cards).</summary>
    public double BandHeight { get; set; }
    /// <summary>Glide after new samples (small graphs always, large ones with smoothBig). Off = jump.</summary>
    public bool Glide { get; set; }
    /// <summary>Bigger hover label (13 pt) and peak clicks: L/XL cards and the detail screen.</summary>
    public bool Full { get => full; set { full = value; hoverText.FontSize = value ? 13 : 12; } }
    /// <summary>Raised when a peak is clicked in full mode (the card tap is then suppressed).</summary>
    public event Action<PeakSpan, Point>? PeakClicked;

    public Sparkline()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);   // hit-testing for the hover; clicks still reach the card
        plot.Children.Add(selected);
        plot.Children.Add(area); plot.Children.Add(line2); plot.Children.Add(line);
        Children.Add(plot);
        Children.Add(hoverLine);
        hoverText.Inlines.Add(runTime); hoverText.Inlines.Add(new Run { Text = "  " }); hoverText.Inlines.Add(runValue); hoverText.Inlines.Add(runCause);
        hoverText.Foreground = ThemeService.Brush("FuseboxOnRow");
        Children.Add(hoverText);
        runTime.Foreground = ThemeService.Brush("FuseboxOnRowSecondary");
        runCause.Foreground = ThemeService.Brush("FuseboxAccent");
        hoverText.FontSize = 12;
        SizeChanged += (_, _) => { plot.Margin = new Thickness(0, BandHeight, 0, 0); Redraw(); };
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => HideHover();
        Tapped += OnTapped;
        Unloaded += (_, _) => gliding = false;
    }

    /// <summary>Compatibility for pages that just plot one series: no glide, no second line.</summary>
    public void SetValues(IReadOnlyList<double> v, double maxValue = double.NaN) => Update(v, null, maxValue);

    /// <summary>The graph's colour (default: the theme's graph brush).</summary>
    public Microsoft.UI.Xaml.Media.Brush Stroke { get => line.Stroke; set => line.Stroke = value; }

    public void SetHover(HoverText? provider) => hover = provider;

    public void SetPeaks(IReadOnlyList<PeakSpan> spans, PeakSpan? selection = null)
    {
        peaks = spans; selectedPeak = selection;
        UpdateSelected();
    }

    /// <summary>New data. <paramref name="top"/> NaN = scale to the data (min 1). Glides when allowed and the series moved by one step.</summary>
    public void Update(IReadOnlyList<double> values, IReadOnlyList<double>? secondValues = null, double top = 100, double bottom = 0)
    {
        double newTop = double.IsNaN(top) ? SparkGeometry.AutoTop(values, secondValues) : top;
        var kind = GlideKind.None;
        double w = ActualWidth;
        if (Glide && Anim.Allowed && w > 0 && !SparkGeometry.NeedsThinning(values.Count, w))
            kind = SparkGeometry.Detect(main, values);
        if (kind != GlideKind.None)
        {
            // Continue from the old data; if the previous glide is still running the new one starts from its end state (no jump back).
            glideFrom = [.. main]; glideFromSecond = [.. second]; glideTopFrom = this.top; glideStart = Anim.Now; glide = kind;
            if (!gliding) { gliding = true; Anim.Run(GlideStep); }
        }
        else { gliding = false; glide = GlideKind.None; }
        main = values; second = secondValues ?? [];
        this.top = newTop; this.bottom = bottom;
        drawnShift = drawnStep = double.NaN;
        if (revealPending && values.Count >= 2) { revealPending = false; StartReveal(); }
        Redraw();
        UpdateSelected();
        if (hoverIndex >= 0) ShowHoverAt(hoverIndex);
    }

    bool GlideStep(double now)
    {
        if (!gliding || !IsLoaded) { gliding = false; return false; }
        double p = Math.Clamp((now - glideStart) / SparkGeometry.GlideSeconds, 0, 1);
        DrawGlide(p);
        if (p >= 1) { gliding = false; glide = GlideKind.None; return false; }
        return true;
    }

    void Redraw()
    {
        if (gliding) { DrawGlide(Math.Clamp((Anim.Now - glideStart) / SparkGeometry.GlideSeconds, 0, 1), force: true); return; }
        Draw(main, second, top, 0, double.NaN);
    }

    void DrawGlide(double p, bool force = false)
    {
        double w = ActualWidth, dpi = XamlRoot?.RasterizationScale ?? 1;
        int n = glideFrom.Count;
        if (w <= 0 || n < 2 || p >= 1) { drawnShift = drawnStep = double.NaN; Draw(main, second, top, 0, double.NaN); return; }
        double t = glideTopFrom + (top - glideTopFrom) * p;
        if (glide == GlideKind.Slide)
        {
            double st = w / (n - 1), shift = -(1 - p) * st;
            if (!force && !double.IsNaN(drawnShift) && !SparkGeometry.ShouldRedraw(shift - drawnShift, dpi) && Math.Abs(t - top) < 1e-9) return;
            drawnShift = shift;
            var a = new List<double>(glideFrom) { main[^1] };
            var b = second.Count > 0 && glideFromSecond.Count > 0 ? new List<double>(glideFromSecond) { second[^1] } : [];
            Draw(a, b, t, shift, st);
        }
        else
        {
            double s0 = w / (n - 1), s1 = w / n, st = s0 + (s1 - s0) * p;
            if (!force && !double.IsNaN(drawnStep) && !SparkGeometry.ShouldRedraw((st - drawnStep) * n, dpi) && Math.Abs(t - top) < 1e-9) return;
            drawnStep = st;
            Draw(main, second, t, 0, st, leftAligned: true);
        }
    }

    /// <summary>Builds the three paths. step NaN = even spacing over the full width.</summary>
    void Draw(IReadOnlyList<double> a, IReadOnlyList<double> b, double topValue, double shift, double stepPx, bool leftAligned = false)
    {
        double w = ActualWidth, h = Math.Max(ActualHeight - BandHeight, 0);
        if (w <= 0 || h <= 0 || a.Count < 2) { area.Data = null; line.Data = null; line2.Data = null; UpdateClip(); return; }
        var va = SparkGeometry.Thin(a, w);
        thinned = va.Count != a.Count;
        var pa = Pts(va, w, h, topValue, shift, stepPx, leftAligned);
        line.Data = Geometry(pa, false, h);
        area.Data = Geometry(pa, true, h);
        if (b.Count >= 2)
        {
            var pb = Pts(SparkGeometry.Thin(b, w), w, h, topValue, shift, stepPx, leftAligned);
            line2.Data = Geometry(pb, false, h);
        }
        else line2.Data = null;
        UpdateClip();
    }

    List<(double X, double Y)> Pts(IReadOnlyList<double> v, double w, double h, double topValue, double shift, double stepPx, bool leftAligned)
    {
        if (!leftAligned) return SparkGeometry.Points(v, w, h, topValue, bottom, shift, double.IsNaN(stepPx) ? 0 : stepPx);
        var p = new List<(double, double)>(v.Count);
        for (int i = 0; i < v.Count; i++) p.Add((i * stepPx, SparkGeometry.Y(v[i], h, topValue, bottom)));
        return p;
    }

    static Geometry Geometry(List<(double X, double Y)> pts, bool closed, double h)
    {
        var fig = new PathFigure { StartPoint = new Point(pts[0].X, pts[0].Y), IsClosed = closed, IsFilled = closed };
        foreach (var s in SparkGeometry.Rounded(pts))
            fig.Segments.Add(s.Quad
                ? new QuadraticBezierSegment { Point1 = new Point(s.Cx, s.Cy), Point2 = new Point(s.X, s.Y) }
                : new LineSegment { Point = new Point(s.X, s.Y) });
        if (closed)
        {
            fig.Segments.Add(new LineSegment { Point = new Point(pts[^1].X, h) });
            fig.Segments.Add(new LineSegment { Point = new Point(pts[0].X, h) });
        }
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    // ---- reveal: the plot is clipped by a rectangle growing from the right edge (cubic-bezier(0.22, 1, 0.36, 1)) ----

    /// <summary>Intro: reveal from the right after the order-th delay. Waits for the second sample when there is no line yet.</summary>
    public void Reveal(int order, double pace)
    {
        revealOrder = order;
        revealP = 0; UpdateClip();
        if (main.Count < 2) { revealPending = true; revealPace = pace; return; }
        revealPace = pace;
        StartReveal();
    }

    double revealPace = 1;
    readonly RectangleGeometry clip = new();

    void StartReveal() =>
        Anim.Tween(Motion.RevealDuration(revealPace), Motion.RevealDelay(revealOrder, revealPace), p => { revealP = Motion.RevealEase(p); UpdateClip(); }, () => { revealP = 1; UpdateClip(); });

    void UpdateClip()
    {
        double w = ActualWidth, h = Math.Max(ActualHeight - BandHeight, 0);
        double shown = w * revealP;
        clip.Rect = new Rect(w - shown, 0, Math.Max(shown, 0), h);
        plot.Clip ??= clip;
    }

    // ---- peaks, hover ----

    void UpdateSelected()
    {
        double w = ActualWidth, h = Math.Max(ActualHeight - BandHeight, 0);
        if (selectedPeak == null || main.Count < 2 || w <= 0) { selected.Visibility = Visibility.Collapsed; return; }
        double st = w / (main.Count - 1), x0 = selectedPeak.From * st, x1 = selectedPeak.To * st;
        selected.Visibility = Visibility.Visible;
        selected.Width = Math.Max(x1 - x0, 4); selected.Height = h;
        Canvas.SetLeft(selected, x0 - (Math.Max(x1 - x0, 4) - (x1 - x0)) / 2);
    }

    int IndexAt(double x) => thinned
        ? Math.Clamp((int)Math.Round(SparkGeometry.Nearest(x, ActualWidth, Math.Max((int)ActualWidth, 2)) * (main.Count - 1.0) / Math.Max((int)ActualWidth - 1, 1)), 0, main.Count - 1)
        : SparkGeometry.Nearest(x, ActualWidth, main.Count);

    PeakSpan? PeakAt(double x)
    {
        if (!full || main.Count < 2) return null;
        double st = ActualWidth / (main.Count - 1);
        return peaks.FirstOrDefault(p => x >= p.From * st - 5 && x <= p.To * st + 5);
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (hover == null || main.Count < 2 || ActualWidth <= 0) return;
        var x = e.GetCurrentPoint(this).Position.X;
        ShowHoverAt(IndexAt(x));
        ProtectedCursor = PeakAt(x) != null ? InputSystemCursor.Create(InputSystemCursorShape.Hand) : null;
    }

    void ShowHoverAt(int i)
    {
        if (hover == null || i < 0 || i >= main.Count) return;
        hoverIndex = i;
        double w = ActualWidth;
        double x = main.Count > 1 ? w - (main.Count - 1 - i) * (w / (main.Count - 1)) : w;
        hoverLine.Margin = new Thickness(Math.Clamp(x, 0, w - 1), 0, 0, 0);
        hoverLine.Height = Math.Max(ActualHeight - BandHeight, 0);
        hoverLine.Visibility = Visibility.Visible;
        var (time, value, cause) = hover(i);
        runTime.Text = time; runValue.Text = value; runCause.Text = full && cause != null ? "  " + cause : "";
        hoverText.Visibility = Visibility.Visible;
    }

    void HideHover()
    {
        hoverIndex = -1;
        hoverLine.Visibility = Visibility.Collapsed;
        hoverText.Visibility = Visibility.Collapsed;
        ProtectedCursor = null;
    }

    void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (PeakAt(pos.X) is { } p) { e.Handled = true; PeakClicked?.Invoke(p, pos); }
    }
}
