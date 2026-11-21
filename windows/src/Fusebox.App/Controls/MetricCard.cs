using Fusebox.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Controls;

/// <summary>One Live card: name, big value, small facts, 5-minute graph. Click = detail page. Updated only from LivePage.Render.</summary>
public sealed partial class MetricCard : Border
{
    public Metric Metric { get; }
    public event Action<Metric>? Clicked;

    readonly TextBlock title = new() { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.7 };
    readonly TextBlock value = new() { FontSize = 28, FontWeight = FontWeights.SemiBold };
    readonly TextBlock unit = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) };
    readonly StackPanel facts = new() { Spacing = 2 };
    readonly Sparkline spark = new() { Height = 44, Margin = new Thickness(0, 8, 0, 0) };

    public MetricCard(Metric m)
    {
        Metric = m;
        Style = (Style)Application.Current.Resources["FuseboxCard"];
        title.Text = MetricText.Title(m);
        var big = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        big.Children.Add(value);
        big.Children.Add(unit);
        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(big);
        stack.Children.Add(facts);
        stack.Children.Add(spark);
        Child = stack;
        Tapped += (_, _) => Clicked?.Invoke(Metric);
    }

    /// <param name="wide">Show all facts (the window is wide enough); otherwise the first two.</param>
    public void Update(MetricText.Card c, IReadOnlyList<double> graph, bool wide)
    {
        value.Text = c.Value;
        unit.Text = c.Unit;
        int n = wide ? c.Facts.Length : Math.Min(2, c.Facts.Length);
        while (facts.Children.Count > n) facts.Children.RemoveAt(facts.Children.Count - 1);
        while (facts.Children.Count < n) facts.Children.Add(new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        for (int i = 0; i < n; i++) ((TextBlock)facts.Children[i]).Text = c.Facts[i];
        spark.SetValues(graph, Metrics.Scale(Metric));
    }
}
