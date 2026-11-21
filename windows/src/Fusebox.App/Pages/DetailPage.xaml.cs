using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>One metric in detail: 5 min / 1 hour / today, min/avg/max, extra facts, per-core bars (CPU), peaks (CPU/GPU/RAM).</summary>
public sealed partial class DetailPage : Page
{
    Metric metric;
    Period period = Period.Minutes5;
    readonly List<(TextBlock Label, TextBlock Value)> factRows = [];
    readonly List<(ProgressBar Bar, TextBlock Value)> coreRows = [];

    public DetailPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
        var periods = new[] { Period.Minutes5, Period.Hour, Period.Today };
        for (int i = 0; i < 3; i++) ((RadioButton)PeriodPick.Items[i]).Content = MetricText.PeriodTitle(periods[i]);
        PeriodPick.SelectedIndex = 0;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        metric = e.Parameter is Metric m ? m : Metric.Cpu;
        TitleText.Text = MetricText.Title(metric);
        App.Live.Updated += Render;
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Live.Updated -= Render;

    void Back_Click(object sender, RoutedEventArgs e) { if (Frame.CanGoBack) Frame.GoBack(); }

    void Period_Changed(object sender, SelectionChangedEventArgs e)
    {
        period = (Period)Math.Max(PeriodPick.SelectedIndex, 0);
        Render();
    }

    void Render()
    {
        if (App.Live.Last is not { } s) return;
        var series = App.Live.Series[metric];
        int step = period == Period.Today ? 60 : LiveHub.StepSeconds;
        var values = series.Values(period, LiveHub.StepSeconds);

        var card = MetricText.Summary(metric, s, App.Live);
        ValueText.Text = card.Value;
        UnitText.Text = card.Unit;

        Graph.SetValues(values, Metrics.Scale(metric));
        Collecting.Visibility = values.Count < 2 ? Visibility.Visible : Visibility.Collapsed;
        var (min, avg, max) = MetricSeries.Stats(values);
        MinText.Text = MetricText.Format(metric, min);
        AvgText.Text = MetricText.Format(metric, avg);
        MaxText.Text = MetricText.Format(metric, max);

        var rows = MetricText.Details(metric, s, App.Live, values, MetricText.PeriodTitle(period), step);
        while (factRows.Count < rows.Count)
        {
            var l = new TextBlock { Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
            var v = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, TextWrapping = TextWrapping.Wrap };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(v, 1);
            g.Children.Add(l); g.Children.Add(v);
            FactsPanel.Children.Add(g);
            factRows.Add((l, v));
        }
        while (factRows.Count > rows.Count) { FactsPanel.Children.RemoveAt(factRows.Count - 1); factRows.RemoveAt(factRows.Count - 1); }
        for (int i = 0; i < rows.Count; i++) { factRows[i].Label.Text = rows[i].Label; factRows[i].Value.Text = rows[i].Value; }

        RenderCores(s);
        RenderPeaks();
    }

    void RenderCores(LiveSample s)
    {
        CoresCard.Visibility = metric == Metric.Cpu && s.Cores.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (metric != Metric.Cpu || s.Cores.Length == 0) return;
        var hw = App.Live.Hardware;
        if (coreRows.Count != s.Cores.Length)
        {
            coreRows.Clear();
            CoreGrid.Children.Clear();
            CoreGrid.ColumnDefinitions.Clear();
            CoreGrid.RowDefinitions.Clear();
            CoreGrid.ColumnDefinitions.Add(new ColumnDefinition());
            CoreGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < s.Cores.Length; i++)
            {
                if (i % 2 == 0) CoreGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var cell = new Grid { ColumnSpacing = 8 };
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                cell.ColumnDefinitions.Add(new ColumnDefinition());
                cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                bool eff = i < hw.EfficiencyLogical.Length && hw.EfficiencyLogical[i];
                var name = new TextBlock { Text = (hw.EfficiencyCores > 0 ? (eff ? "E" : "P") : "") + i, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
                var bar = new ProgressBar { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
                var val = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(bar, 1); Grid.SetColumn(val, 2);
                cell.Children.Add(name); cell.Children.Add(bar); cell.Children.Add(val);
                Grid.SetColumn(cell, i % 2); Grid.SetRow(cell, i / 2);
                CoreGrid.Children.Add(cell);
                coreRows.Add((bar, val));
            }
        }
        CoresTitle.Text = hw.EfficiencyCores > 0
            ? L("Per core · %lld efficiency + %lld performance", hw.EfficiencyCores, hw.PerformanceCores)
            : L("Per core · %lld cores", s.Cores.Length);
        for (int i = 0; i < s.Cores.Length; i++) { coreRows[i].Bar.Value = s.Cores[i]; coreRows[i].Value.Text = Fmt.Percent(s.Cores[i]); }
    }

    void RenderPeaks()
    {
        PeakKind? kind = metric switch { Metric.Cpu => PeakKind.Cpu, Metric.Gpu => PeakKind.Gpu, Metric.Ram => PeakKind.Mem, _ => null };
        PeaksCard.Visibility = kind == null ? Visibility.Collapsed : Visibility.Visible;
        if (kind == null) return;
        var since = period switch { Period.Minutes5 => DateTime.Now.AddMinutes(-5), Period.Hour => DateTime.Now.AddHours(-1), _ => DateTime.Today };
        var list = App.Live.Peaks.Today.Where(p => p.Kind == kind && p.End >= since).OrderByDescending(p => p.Start).Take(8).ToList();
        PeaksPanel.Children.Clear();
        if (list.Count == 0) PeaksPanel.Children.Add(new TextBlock { Text = L("No peaks in this period"), Opacity = 0.6 });
        foreach (var p in list)
            PeaksPanel.Children.Add(new TextBlock { Text = $"{p.At:HH:mm}  ·  {Fmt.Percent(p.Top)}  ·  {p.Seconds} s{(p.App is { } a ? "  ·  " + a : "")}" });
    }
}
