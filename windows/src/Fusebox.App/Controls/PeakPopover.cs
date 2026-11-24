using Fusebox.Core;
using Fusebox.Core.Live;
using Fusebox.Core.Processes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Controls;

/// <summary>
/// The transient popover of a clicked peak (§3.9, 380 wide, padding 14): "CPU peak 14:32 · 94% · 8 s", a subtitle saying where the apps come
/// from (snapshot at the moment, or the minute measurement), and four columns CPU / MEMORY / DISK / GPU with the top 3 apps each.
/// Windows measures no disk use per app, so DISK shows "–".
/// </summary>
public static class PeakPopover
{
    public static void Show(FrameworkElement anchor, Point at, Peak p)
    {
        IReadOnlyList<AppGroup>? groups = null;
        string subtitle;
        if (PeakSnapshots.Near(p) is { } snap) { groups = snap.Groups; subtitle = L("Apps at that moment"); }
        else if (App.Live.Apps is { } minute && (DateTime.Now - p.At).TotalSeconds < 90) { groups = minute; subtitle = L("Apps that minute (no snapshot at the moment itself)"); }
        else subtitle = L("No app measurement near this moment");

        var panel = new StackPanel { Spacing = 10, Width = 380 - 28 };
        var head = new TextBlock
        {
            Text = L("%@ peak %@ · %@ · %@", Peak.Title(p.Kind), $"{p.At:HH:mm}", PeakInfo.Value(p), PeakInfo.Length(p.Seconds)),
            Foreground = ThemeService.Brush("FuseboxText"), TextWrapping = TextWrapping.Wrap,
        };
        Look.Label(head, 14); head.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var sub = new TextBlock { Text = subtitle, Foreground = ThemeService.Brush("FuseboxSecondary"), TextWrapping = TextWrapping.Wrap };
        Look.Label(sub, 12);
        panel.Children.Add(head); panel.Children.Add(sub);

        if (groups != null)
        {
            var real = groups.Where(g => g.Kind != AppKind.Windows).ToList();
            var grid = new Grid { ColumnSpacing = 18, RowSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
            var cols = new (string Title, Func<AppGroup, double> By, Func<double, string> Fmt1)[]
            {
                ("CPU", g => g.Cpu, Fmt.Pct), (L("Memory"), g => g.Ram, b => Fmt.Bytes(b)), (L("Disk"), _ => 0, _ => "–"), ("GPU", g => g.Gpu, Fmt.Pct),
            };
            for (int i = 0; i < 4; i++)
            {
                var (title, by, f) = cols[i];
                var col = new StackPanel { Spacing = 3 };
                var t = new TextBlock { Text = title.ToUpperInvariant(), Foreground = ThemeService.Brush("FuseboxSecondary") };
                Look.Label(t, 11); t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                col.Children.Add(t);
                var top = real.OrderByDescending(by).Where(g => by(g) > 0).Take(3).ToList();
                if (top.Count == 0) col.Children.Add(Cell("–", ""));
                foreach (var g in top) col.Children.Add(Cell(g.Name, f(by(g))));
                Grid.SetColumn(col, i % 2); Grid.SetRow(col, i / 2);
                grid.Children.Add(col);
            }
            panel.Children.Add(grid);
        }
        var fly = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };
        fly.ShowAt(anchor, new FlyoutShowOptions { Position = at, Placement = FlyoutPlacementMode.Bottom });
    }

    static Grid Cell(string name, string value)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var n = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Foreground = ThemeService.Brush("FuseboxText") };
        var v = new TextBlock { Text = value, Foreground = ThemeService.Brush("FuseboxAccent") };
        Look.Label(n, 12); Look.Number(v, 12);
        Grid.SetColumn(v, 1);
        g.Children.Add(n); g.Children.Add(v);
        return g;
    }
}
