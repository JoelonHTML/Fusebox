using Fusebox.Core.Live;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace Fusebox.App.Controls;

/// <summary>
/// One bar per logical core (§3.6, §10.11): efficiency cores first, then a gap, then performance cores. Track = accent 15 %, fill = accent
/// (white on Color blocks) scaled from the bottom. Header version: fixed bars (6 wide at L/XL, 4 smaller, spacing 2); wide version
/// (detail screen): bars share the width, spacing 3, 54 high. Moves toward new values in 6 smoothstep steps on the shared clock.
/// </summary>
public sealed partial class CoreBars : Canvas
{
    readonly List<Rectangle> tracks = [], fills = [];
    double[] shown = [], target = [], prev = [];
    int[] order = [];
    int efficient;
    bool wide;
    double barWidth = 4, barHeight = 14, version;

    public CoreBars() => SizeChanged += (_, _) => { if (wide) Layout(); };

    /// <summary>Header bars (card): bar width and height in DIPs. Pass wide = true for the detail screen (54 high, bars fill the width).</summary>
    public void Configure(bool wideMode, double width, double height)
    {
        wide = wideMode; barWidth = width; barHeight = height;
        Height = height;
        Layout();
    }

    /// <summary>Values 0–100 per logical core; <paramref name="efficiencyLogical"/> as in HardwareInfo (true = E-core), may be empty.</summary>
    public void Set(double[] loads, bool[] efficiencyLogical)
    {
        if (loads.Length != target.Length)
        {
            target = [.. loads]; shown = new double[loads.Length]; prev = new double[loads.Length];
            var e = Enumerable.Range(0, loads.Length).Where(i => i < efficiencyLogical.Length && efficiencyLogical[i]).ToList();
            var p = Enumerable.Range(0, loads.Length).Where(i => !e.Contains(i)).ToList();
            efficient = e.Count == loads.Length ? 0 : e.Count;
            order = [.. e, .. p];
            while (tracks.Count < loads.Length)
            {
                var t = new Rectangle { Fill = ThemeService.Brush("FuseboxGraphFill"), RadiusX = 1, RadiusY = 1 };
                var f = new Rectangle { Fill = ThemeService.Brush("FuseboxGraph"), RadiusX = 1, RadiusY = 1 };
                tracks.Add(t); fills.Add(f); Children.Add(t); Children.Add(f);
            }
            while (tracks.Count > loads.Length)
            {
                Children.Remove(tracks[^1]); Children.Remove(fills[^1]); tracks.RemoveAt(tracks.Count - 1); fills.RemoveAt(fills.Count - 1);
            }
            Layout();
            Array.Copy(target, shown, target.Length);
            Apply();
            return;
        }
        prev = [.. shown];
        target = [.. loads];
        if (!Anim.Allowed) { Array.Copy(target, shown, target.Length); Apply(); return; }
        double id = ++version, start = Anim.Now;
        int step = 0;
        Anim.Run(now =>
        {
            if (id != version) return false;                       // a newer sample took over
            int s = Math.Min((int)((now - start) / Motion.GridSeconds) + 1, Motion.FadeSteps);
            if (s == step && s < Motion.FadeSteps) return true;
            step = s;
            double p = Motion.FadeIn(s);
            for (int i = 0; i < shown.Length; i++) shown[i] = prev[i] + (target[i] - prev[i]) * p;
            Apply();
            return s < Motion.FadeSteps;
        });
    }

    /// <summary>Width the header version needs (so the card can reserve it).</summary>
    public double HeaderWidth => tracks.Count == 0 ? 0 : tracks.Count * barWidth + (tracks.Count - 1) * 2 + (efficient > 0 ? 4 : 0);

    void Layout()
    {
        int n = tracks.Count;
        if (n == 0) return;
        double spacing = wide ? 3 : 2, extra = efficient > 0 ? (wide ? 8 : 4) : 0;
        double w = wide ? Math.Max((ActualWidth - (n - 1) * spacing - extra) / n, 2) : barWidth;
        if (!wide) Width = HeaderWidth;
        for (int k = 0; k < n; k++)
        {
            double x = k * (w + spacing) + (efficient > 0 && k >= efficient ? extra : 0);
            tracks[k].Width = fills[k].Width = w;
            tracks[k].Height = barHeight;
            SetLeft(tracks[k], x); SetLeft(fills[k], x); SetTop(tracks[k], 0);
        }
        Apply();
    }

    void Apply()
    {
        for (int k = 0; k < fills.Count && k < order.Length; k++)
        {
            double h = barHeight * Math.Clamp(shown[order[k]], 0, 100) / 100;
            fills[k].Height = h;
            SetTop(fills[k], barHeight - h);
        }
    }
}
