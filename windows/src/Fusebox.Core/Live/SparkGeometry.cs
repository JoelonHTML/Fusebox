namespace Fusebox.Core.Live;

/// <summary>A path command: <see cref="Quad"/> false = line to (X, Y); true = quadratic curve to (X, Y) with control (Cx, Cy).</summary>
public readonly record struct Seg(bool Quad, double Cx, double Cy, double X, double Y);

public enum GlideKind { None, Slide, Grow }

/// <summary>
/// Sparkline geometry (§3.5, §10.10): right-aligned series over the full width, thinning, y scale, rounded polyline (corners cut 20 %,
/// quadratic through the vertex), glide plans. Pure, so the shared timer only moves numbers.
/// </summary>
public static class SparkGeometry
{
    public const double Roundness = 0.2, GlideSeconds = 1.9, RedrawPixels = 0.5;

    /// <summary>More than 2 points per pt of width → bucket average down to max(width, 2) points (and no glide).</summary>
    public static bool NeedsThinning(int count, double width) => count > width * 2;

    public static List<double> Thin(IReadOnlyList<double> v, double width)
    {
        if (!NeedsThinning(v.Count, width)) return [.. v];
        int n = Math.Max((int)width, 2);
        var o = new List<double>(n);
        for (int i = 0; i < n; i++)
        {
            int a = (int)((long)i * v.Count / n), b = Math.Max((int)((long)(i + 1) * v.Count / n), a + 1);
            double s = 0; int c = 0;
            for (int j = a; j < b && j < v.Count; j++) if (double.IsFinite(v[j])) { s += v[j]; c++; }
            o.Add(c == 0 ? 0 : s / c);
        }
        return o;
    }

    /// <summary>Auto scale: the larger of both series, at least 1.</summary>
    public static double AutoTop(IReadOnlyList<double> a, IReadOnlyList<double>? b)
    {
        double m = 1;
        foreach (var x in a) if (double.IsFinite(x) && x > m) m = x;
        if (b != null) foreach (var x in b) if (double.IsFinite(x) && x > m) m = x;
        return m;
    }

    /// <summary>y = height − height × clamp((v − bottom) / (top − bottom), 0, 1); NaN counts as the bottom.</summary>
    public static double Y(double v, double height, double top, double bottom = 0)
    {
        double range = top - bottom;
        double f = range <= 0 || !double.IsFinite(v) ? 0 : Math.Clamp((v - bottom) / range, 0, 1);
        return height - height * f;
    }

    /// <summary>
    /// Points of a series, newest on the right edge. <paramref name="shift"/> moves everything left (px) for a glide; with
    /// <paramref name="lead"/> extra points to the left (the old oldest values slide out). One value = one point at the right edge.
    /// </summary>
    public static List<(double X, double Y)> Points(IReadOnlyList<double> v, double width, double height, double top, double bottom = 0, double shift = 0, double step = 0)
    {
        var p = new List<(double, double)>(v.Count);
        int n = v.Count;
        if (n == 0) return p;
        if (step <= 0) step = n > 1 ? width / (n - 1) : 0;
        for (int i = 0; i < n; i++) p.Add((width - (n - 1 - i) * step - shift, Y(v[i], height, top, bottom)));
        return p;
    }

    public static (double X, double Y) Start(IReadOnlyList<(double X, double Y)> p) => p.Count > 0 ? p[0] : (0, 0);

    /// <summary>The rounded polyline (not a spline): each inner vertex becomes a line to 20 % before it and a quad to 20 % after it.</summary>
    public static List<Seg> Rounded(IReadOnlyList<(double X, double Y)> p, double k = Roundness)
    {
        var s = new List<Seg>();
        int n = p.Count;
        if (n < 2) return s;
        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        for (int i = 1; i < n - 1; i++)
        {
            s.Add(new(false, 0, 0, Lerp(p[i].X, p[i - 1].X, k), Lerp(p[i].Y, p[i - 1].Y, k)));
            s.Add(new(true, p[i].X, p[i].Y, Lerp(p[i].X, p[i + 1].X, k), Lerp(p[i].Y, p[i + 1].Y, k)));
        }
        s.Add(new(false, 0, 0, p[n - 1].X, p[n - 1].Y));
        return s;
    }

    /// <summary>Slide: the new series is the old one minus its first value plus one new value. Grow: the old one plus one value. Else None.</summary>
    public static GlideKind Detect(IReadOnlyList<double> old, IReadOnlyList<double> now)
    {
        int n = old.Count;
        if (n < 2 || now.Count < 2) return GlideKind.None;
        static bool Same(double a, double b) => a == b || Math.Abs(a - b) < 1e-9 || (double.IsNaN(a) && double.IsNaN(b));
        if (now.Count == n)
        {
            for (int i = 0; i < n - 1; i++) if (!Same(old[i + 1], now[i])) return GlideKind.None;
            return GlideKind.Slide;
        }
        if (now.Count == n + 1)
        {
            for (int i = 0; i < n; i++) if (!Same(old[i], now[i])) return GlideKind.None;
            return GlideKind.Grow;
        }
        return GlideKind.None;
    }

    /// <summary>Redraw only when the line moved at least half a device pixel since the last drawn state.</summary>
    public static bool ShouldRedraw(double movedDip, double dpiScale) => Math.Abs(movedDip) * dpiScale >= RedrawPixels;

    /// <summary>Hover: index of the point nearest to x (0..width) among n right-aligned points.</summary>
    public static int Nearest(double x, double width, int n)
    {
        if (n <= 1) return 0;
        return Math.Clamp((int)Math.Round((n - 1) + (x - width) / (width / (n - 1))), 0, n - 1);
    }
}

/// <summary>Timing helpers for the animations (§10.9–10.12). All run on one shared clock in the App.</summary>
public static class Motion
{
    public const int FadeSteps = 6;
    public const double GridSeconds = 0.0625, IntroSeconds = 1.2;

    /// <summary>p²(3−2p).</summary>
    public static double Smoothstep(double p) { p = Math.Clamp(p, 0, 1); return p * p * (3 - 2 * p); }

    /// <summary>Opacity of the new number after <paramref name="step"/> of 6 (old = 1 − this).</summary>
    public static double FadeIn(int step) => Smoothstep((double)step / FadeSteps);

    /// <summary>Intro (row entrance, reveal) only within 1.2 s after launch or tab switch.</summary>
    public static bool IntroActive(double secondsSinceStart) => secondsSinceStart >= 0 && secondsSinceStart <= IntroSeconds;

    /// <summary>Pace 1 at launch, 0.4 after a tab switch.</summary>
    public static double Pace(bool launch) => launch ? 1 : 0.4;

    /// <summary>Row entrance delay in seconds: (0.03 + index × 0.04) × pace.</summary>
    public static double RowDelay(int index, double pace) => (0.03 + index * 0.04) * pace;

    /// <summary>Graph reveal delay: (0.05 + order × 0.25) × pace; duration 0.8 × pace.</summary>
    public static double RevealDelay(int order, double pace) => (0.05 + order * 0.25) * pace;
    public static double RevealDuration(double pace) => 0.8 * pace;

    /// <summary>CSS-style cubic-bezier easing y(x) for control points (x1,y1) (x2,y2), e.g. (0.22, 1, 0.36, 1).</summary>
    public static double CubicBezier(double x1, double y1, double x2, double y2, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double B(double a, double b, double t) => 3 * a * (1 - t) * (1 - t) * t + 3 * b * (1 - t) * t * t + t * t * t;
        double lo = 0, hi = 1, t = x;
        for (int i = 0; i < 24; i++)
        {
            t = (lo + hi) / 2;
            if (B(x1, x2, t) < x) lo = t; else hi = t;
        }
        return B(y1, y2, t);
    }

    /// <summary>The reveal curve cubic-bezier(0.22, 1, 0.36, 1).</summary>
    public static double RevealEase(double x) => CubicBezier(0.22, 1, 0.36, 1, x);
}
