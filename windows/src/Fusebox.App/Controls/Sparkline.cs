using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Fusebox.App.Controls;

/// <summary>
/// Cheap line graph: one Polyline, redrawn only when <see cref="SetValues"/> is called (no animation, no timer).
/// Max = NaN scales to the largest value.
/// </summary>
public sealed partial class Sparkline : Grid
{
    readonly Polyline line = new() { StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
    IReadOnlyList<double> values = [];
    double max = double.NaN;

    public Sparkline()
    {
        line.Stroke = Application.Current.Resources.TryGetValue("FuseboxGraphStroke", out var b) && b is Brush brush
            ? brush : new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        Children.Add(line);
        SizeChanged += (_, _) => Draw();
    }

    public Brush Stroke { get => line.Stroke; set => line.Stroke = value; }

    public void SetValues(IReadOnlyList<double> v, double maxValue = double.NaN)
    {
        values = v; max = maxValue;
        Draw();
    }

    void Draw()
    {
        double w = ActualWidth, h = ActualHeight;
        var pts = new PointCollection();
        if (values.Count > 1 && w > 0 && h > 0)
        {
            double top = double.IsNaN(max) ? Math.Max(values.Max(), 1e-9) : max;
            for (int i = 0; i < values.Count; i++)
            {
                double v = double.IsNaN(values[i]) ? 0 : Math.Clamp(values[i] / top, 0, 1);
                pts.Add(new Windows.Foundation.Point(w * i / (values.Count - 1), h - 1 - v * (h - 2)));
            }
        }
        line.Points = pts;
    }
}
