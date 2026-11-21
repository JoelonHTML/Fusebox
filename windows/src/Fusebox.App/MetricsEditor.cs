using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App;

/// <summary>
/// Which measurements show, and in what order (Settings and Setup). Prefs "metrics" = enabled ids in order, comma list
/// (cpu,gpu,ram,net,ssd,bat); "metricsHidden" = the order of the disabled ones, so the order survives toggling.
/// </summary>
public static class MetricsEditor
{
    public const string Default = "cpu,gpu,ram,net,ssd,bat";
    static readonly (string Id, string Name)[] All =
    [
        ("cpu", "Processor (CPU)"), ("gpu", "Graphics chip (GPU)"), ("ram", "Memory (RAM)"),
        ("net", "Internet (NET)"), ("ssd", "Free storage (SSD)"), ("bat", "Battery"),
    ];

    static List<string> Split(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(i => All.Any(a => a.Id == i)).Distinct().ToList();
    public static List<string> Enabled => Split(Prefs.Get("metrics", Default));

    static List<string> Order()
    {
        var on = Enabled;
        var hidden = Split(Prefs.Get("metricsHidden", "")).Where(i => !on.Contains(i)).ToList();
        return [.. on, .. hidden, .. All.Select(a => a.Id).Where(i => !on.Contains(i) && !hidden.Contains(i))];
    }

    static void Save(List<string> order, ICollection<string> on)
    {
        Prefs.Set("metrics", string.Join(',', order.Where(on.Contains)));
        Prefs.Set("metricsHidden", string.Join(',', order.Where(i => !on.Contains(i))));
    }

    public static StackPanel Create()
    {
        var p = new StackPanel { Spacing = 2 };
        Fill(p);
        return p;
    }

    static void Fill(StackPanel p)
    {
        p.Children.Clear();
        var order = Order();
        var on = Enabled;
        for (int i = 0; i < order.Count; i++)
        {
            string id = order[i];
            int at = i;
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new CheckBox { Content = Strings.L(All.First(a => a.Id == id).Name), IsChecked = on.Contains(id) };
            box.Click += (_, _) =>
            {
                var cur = Enabled;
                if (box.IsChecked == true) { if (!cur.Contains(id)) cur.Add(id); } else cur.Remove(id);
                Save(Order(), cur);
            };
            row.Children.Add(box);
            row.Children.Add(Move("", 1, at > 0, () => Swap(p, at, at - 1)));
            row.Children.Add(Move("", 2, at < order.Count - 1, () => Swap(p, at, at + 1)));
            p.Children.Add(row);
        }
    }

    static Button Move(string glyph, int column, bool enabled, Action click)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, IsEnabled = enabled, Padding = new Thickness(8, 4, 8, 4) };
        Grid.SetColumn(b, column);
        b.Click += (_, _) => click();
        return b;
    }

    static void Swap(StackPanel p, int a, int b)
    {
        var o = Order();
        (o[a], o[b]) = (o[b], o[a]);
        Save(o, Enabled);
        Fill(p);
    }
}
