using Fusebox.App.Controls;
using Fusebox.Core;
using Fusebox.Core.Live;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>Navigation parameter of the detail screen: the metric, and optionally the peak (start time) to select.</summary>
public sealed record DetailRequest(Metric Metric, DateTime? PeakStart = null);

/// <summary>
/// One metric in detail (§3.8), inside the Live tab (Frame navigation; back = chevron or Esc). Header, headline with the other
/// representation, range 5 min | 1 hour | Today, graph card (max(110, 34 % of the height), hover with cause, selected peak highlighted),
/// Min / Average / Max, peaks list (CPU, GPU, RAM, I/O) with cause, per-core bars (CPU) and per-metric facts / top apps.
/// Updates only on LiveHub.Updated while shown.
/// </summary>
public sealed partial class DetailPage : Page
{
    Metric metric;
    TimeRange range = TimeRange.Short;
    Peak? selected;
    bool building;

    // header / headline
    readonly Button back = new() { Width = 26, Height = 26, Padding = new Thickness(0), Background = null, BorderThickness = new Thickness(0), Content = new FontIcon { Glyph = "", FontSize = 13 } };
    readonly TextBlock title = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    ChangingText big = null!;
    readonly TextBlock unit = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock sideText = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock second = new();
    readonly SelectorBar picker = new();
    // graph
    readonly Sparkline spark = new() { BandHeight = 20, Full = true };
    readonly TextBlock collecting = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly Border graphCard = new();
    readonly TextBlock[] stats = [new(), new(), new()];
    // peaks, extra cards
    readonly Border peaksCard = new();
    readonly StackPanel peaksList = new() { Spacing = 2 };
    readonly StackPanel extra = new() { Spacing = 12 };
    readonly Border coresCard = new();
    readonly TextBlock coresTitle = new();
    readonly CoreBars coreBars = new();
    readonly Grid coreLabels = new();
    readonly Border usageCard = new();
    readonly TextBlock usageText = new();
    readonly Border usageFill = new() { CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left, Height = 10 };
    readonly Grid usageBar = new() { Height = 10 };
    readonly RowList facts = new(false), topApps = new(true);
    readonly TextBlock topTitle = new();
    readonly Border topCard = new();
    readonly Border factsCard = new();
    readonly List<(Border Border, int Index)> styled = [];
    string peaksKey = "";
    List<LiveSample> samples = [];
    List<Peak> shownPeaks = [];

    public DetailPage()
    {
        InitializeComponent();
        KeyboardAccelerators.Add(MakeEscape());
        Build();
    }

    KeyboardAccelerator MakeEscape()
    {
        var k = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        k.Invoked += (_, e) => { e.Handled = true; Back(); };
        return k;
    }

    void Back()
    {
        if (Frame.CanGoBack) Frame.GoBack(); else App.Main?.Open("Live");
    }

    // ---- building (once) ----

    static Brush B(string key) => ThemeService.Brush(key);

    Border Card(UIElement child, int index)
    {
        var b = new Border { Child = child };
        ThemedRow.Apply(b, index);
        b.Padding = new Thickness(10);
        styled.Add((b, index));
        return b;
    }

    static TextBlock Label(string text, double pt, string brush = "FuseboxOnRowSecondary", bool semibold = false)
    {
        var t = new TextBlock { Text = text, Foreground = B(brush), TextWrapping = TextWrapping.NoWrap };
        Look.Label(t, pt);
        if (semibold) t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return t;
    }

    void Build()
    {
        building = true;
        // header
        ToolTipService.SetToolTip(back, L("Back to Live (Esc)"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(back, L("Back to Live (Esc)"));
        back.Foreground = B("FuseboxText");
        back.Click += (_, _) => Back();
        title.Foreground = B("FuseboxText");
        Look.Label(title, 15); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        head.Children.Add(back); head.Children.Add(title);

        // headline
        big = new ChangingText(44);
        var hl = new Grid { ColumnSpacing = 6 };
        hl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bigBox = new Viewbox { StretchDirection = StretchDirection.DownOnly, Child = big, VerticalAlignment = VerticalAlignment.Bottom };
        big.Foreground = B("FuseboxText");
        Grid.SetColumn(unit, 1); Grid.SetColumn(sideText, 2);
        hl.Children.Add(bigBox); hl.Children.Add(unit); hl.Children.Add(sideText);
        unit.Foreground = B("FuseboxSecondary"); Look.Number(unit, 18);
        sideText.Foreground = B("FuseboxSecondary"); Look.Number(sideText, 18);
        second.Foreground = B("FuseboxSecondary"); Look.Label(second, 14);

        // range picker
        foreach (var r in new[] { TimeRange.Short, TimeRange.Hour, TimeRange.Today })
            picker.Items.Add(new SelectorBarItem { Text = MetricText.PeriodTitle(r), Tag = r });
        picker.SelectedItem = picker.Items[0];
        picker.SelectionChanged += (s, _) =>
        {
            if (building || s.SelectedItem?.Tag is not TimeRange r) return;
            range = r; selected = null; Render();
        };

        // graph card
        var gg = new Grid();
        collecting.Text = L("Collecting data…"); collecting.Foreground = B("FuseboxOnRowSecondary"); Look.Label(collecting, 12);
        gg.Children.Add(spark); gg.Children.Add(collecting);
        graphCard.Child = gg;
        ThemedRow.Apply(graphCard, 0); graphCard.Padding = new Thickness(10); styled.Add((graphCard, 0));
        spark.PeakClicked += (span, pos) => { if (span.Tag is Peak p) { Select(p); PeakPopover.Show(spark, pos, p); } };

        // stats
        var st = new Grid { ColumnSpacing = 8 };
        string[] names = [L("Min"), L("Average"), L("Max")];
        for (int i = 0; i < 3; i++)
        {
            st.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var col = new StackPanel { Spacing = 0 };
            col.Children.Add(Label(names[i], 11));
            stats[i].Foreground = B("FuseboxOnRow"); Look.Number(stats[i], 16); stats[i].TextWrapping = TextWrapping.NoWrap;
            col.Children.Add(new Viewbox { StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Child = stats[i] });
            var card = Card(col, i + 1);
            Grid.SetColumn(card, i);
            st.Children.Add(card);
        }

        // peaks
        var pk = new StackPanel { Spacing = 4 };
        pk.Children.Add(Label(L("Peaks"), 11));
        pk.Children.Add(peaksList);
        peaksCard.Child = pk;
        ThemedRow.Apply(peaksCard, 4); peaksCard.Padding = new Thickness(10); styled.Add((peaksCard, 4));

        // per-core, usage, facts, top apps
        var cc = new StackPanel { Spacing = 6 };
        coresTitle.Foreground = B("FuseboxOnRowSecondary"); Look.Label(coresTitle, 11);
        coreBars.Configure(true, 0, 54);
        cc.Children.Add(coresTitle); cc.Children.Add(coreBars); cc.Children.Add(coreLabels);
        coresCard.Child = cc;
        ThemedRow.Apply(coresCard, 1); coresCard.Padding = new Thickness(10); styled.Add((coresCard, 1));

        var uc = new StackPanel { Spacing = 6 };
        usageText.Foreground = B("FuseboxOnRow"); Look.Label(usageText, 12);
        usageBar.Children.Add(new Border { Background = B("FuseboxGraphFill"), CornerRadius = new CornerRadius(5), Height = 10 });
        usageFill.Background = B("FuseboxGraph");
        usageBar.Children.Add(usageFill);
        usageBar.SizeChanged += (_, _) => FillUsage();
        uc.Children.Add(usageText); uc.Children.Add(usageBar);
        usageCard.Child = uc;
        ThemedRow.Apply(usageCard, 2); usageCard.Padding = new Thickness(10); styled.Add((usageCard, 2));

        factsCard.Child = facts.Panel;
        ThemedRow.Apply(factsCard, 3); factsCard.Padding = new Thickness(10); styled.Add((factsCard, 3));

        var tc = new StackPanel { Spacing = 6 };
        topTitle.Foreground = B("FuseboxOnRowSecondary"); Look.Label(topTitle, 11);
        tc.Children.Add(topTitle); tc.Children.Add(topApps.Panel);
        topCard.Child = tc;
        ThemedRow.Apply(topCard, 0); topCard.Padding = new Thickness(10); styled.Add((topCard, 0));

        extra.Children.Add(coresCard); extra.Children.Add(usageCard); extra.Children.Add(factsCard); extra.Children.Add(topCard);

        Body.Children.Add(head); Body.Children.Add(hl); Body.Children.Add(second); Body.Children.Add(picker);
        Body.Children.Add(graphCard); Body.Children.Add(st); Body.Children.Add(peaksCard); Body.Children.Add(extra);
        building = false;
    }

    // ---- navigation ----

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var req = e.Parameter as DetailRequest ?? new DetailRequest(e.Parameter is Metric m ? m : Metric.Cpu);
        metric = req.Metric;
        title.Text = MetricText.Title(metric);
        selected = null;
        range = TimeRange.Short;
        if (req.PeakStart is { } start && App.Live.Peaks.Today.FirstOrDefault(p => p.Start == start && LiveCards.KindsOf(metric).Contains(p.Kind)) is { } peak)
        {
            selected = peak;
            range = PeakInfo.RangeFor(peak.Start, DateTime.Now, LiveHub.ShortMinutes);
        }
        building = true;
        picker.SelectedItem = picker.Items[(int)range];
        building = false;
        peaksCard.Visibility = LiveCards.KindsOf(metric).Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        App.Live.Updated += Render;
        ThemeService.ThemeChanged += Restyle;
        Focus(FocusState.Programmatic);
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Live.Updated -= Render;
        ThemeService.ThemeChanged -= Restyle;
    }

    void Restyle()
    {
        foreach (var (b, i) in styled) { ThemedRow.Apply(b, i); b.Padding = new Thickness(10); }
        peaksKey = "";
        Render();
    }

    void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        graphCard.Height = Math.Max(110, e.NewSize.Height * 0.34);
        // The full title if it fits on one line, else the abbreviation.
        double room = e.NewSize.Width - 28 - 26 - 8;
        title.Text = MetricText.Title(metric);
        title.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        if (title.DesiredSize.Width > room) title.Text = Metrics.Label(metric);
        FillUsage();
    }

    // ---- content ----

    void Render()
    {
        if (App.Live.Last is not { } s) return;
        var o = MetricText.Options();
        samples = App.Live.History.Range(range, LiveHub.ShortMinutes, LiveHub.StepSeconds);
        var (main, sec) = LiveCards.Series(metric, samples, o);
        double top = LiveCards.GraphTop(metric, s, o);

        var h = DetailFacts.Headline(metric, s, o);
        big.Template = LiveCards.Card(metric, s, o).Template;
        foreach (var t in big.Children.OfType<TextBlock>()) Look.SetNumber(t, h.BigSize);
        big.Set(h.Value);
        unit.Text = h.Unit; unit.Visibility = h.Unit.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        sideText.Text = h.Side;
        second.Text = h.Second; second.Visibility = h.Second.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // graph + peaks
        var today = App.Live.Peaks.Today;
        var since = range switch { TimeRange.Short => DateTime.Now.AddMinutes(-LiveHub.ShortMinutes), TimeRange.Hour => DateTime.Now.AddHours(-1), _ => DateTime.Today };
        var kinds = LiveCards.KindsOf(metric);
        shownPeaks = today.Where(p => kinds.Contains(p.Kind) && p.End >= since).OrderByDescending(p => p.Start).Take(12).ToList();
        if (selected != null && !shownPeaks.Contains(selected)) selected = null;
        spark.Glide = range == TimeRange.Short && Prefs.Bool("smoothBig");
        spark.SetHover(i => MetricText.Point(metric, samples, i, o, today));
        var spans = SpansOf(shownPeaks);
        spark.SetPeaks(spans, spans.FirstOrDefault(x => x.Tag == selected));
        spark.Update(main, sec, top);
        collecting.Visibility = main.Count < 2 ? Visibility.Visible : Visibility.Collapsed;

        var (lo, avg, hi) = Metrics.Stats(main);
        stats[0].Text = LiveCards.StatText(metric, lo, o); stats[1].Text = LiveCards.StatText(metric, avg, o); stats[2].Text = LiveCards.StatText(metric, hi, o);
        RenderPeaks(kinds.Length > 0);

        // per-metric cards
        var apps = App.Live.Apps;
        var cards = DetailFacts.Cards(metric, s, o, samples, MetricText.PeriodLower(range), range == TimeRange.Today ? 60 : LiveHub.StepSeconds,
            apps, TimeSpan.FromMilliseconds(Environment.TickCount64), ProcessCount(apps));
        RenderCores(s);
        usageCard.Visibility = cards.UsageText != null ? Visibility.Visible : Visibility.Collapsed;
        if (cards.UsageText != null) { usageText.Text = cards.UsageText; usageFraction = cards.UsageFraction; FillUsage(); }
        factsCard.Visibility = cards.Facts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        facts.Set(cards.Facts);
        topCard.Visibility = cards.TopTitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (cards.TopTitle.Length > 0)
        {
            topTitle.Text = cards.TopTitle;
            if (cards.TopApps.Count == 0) topApps.SetText(L("Available after the first minute")); else topApps.Set(cards.TopApps);
        }
    }

    static int ProcessCount(IReadOnlyList<Fusebox.Core.Processes.AppGroup>? apps) => apps?.Sum(g => g.Pids.Count) ?? 0;

    double usageFraction;

    void FillUsage() => usageFill.Width = double.IsFinite(usageFraction) ? Math.Clamp(usageFraction, 0, 1) * usageBar.ActualWidth : 0;

    List<PeakSpan> SpansOf(IReadOnlyList<Peak> peaks)
    {
        var l = new List<PeakSpan>();
        if (samples.Count < 2) return l;
        foreach (var p in peaks)
        {
            int a = 0, b = -1;
            while (a < samples.Count - 1 && samples[a].Time < p.Start) a++;
            for (int i = 0; i < samples.Count; i++) if (samples[i].Time <= p.End) b = i;
            if (b >= a && p.End >= samples[0].Time) l.Add(new(a, Math.Max(b, a + 1), p));
        }
        return l;
    }

    void Select(Peak p)
    {
        selected = selected == p ? null : p;
        var spans = SpansOf(shownPeaks);
        spark.SetPeaks(spans, spans.FirstOrDefault(x => x.Tag == selected));
        peaksKey = "";
        RenderPeaks(true);
    }

    /// <summary>Up to 12 peaks, newest first: time · length · value · cause. Click toggles the highlight in the graph.</summary>
    void RenderPeaks(bool has)
    {
        if (!has) return;
        string key = string.Join('|', shownPeaks.Select(p => $"{p.Start:O}{p.End:O}{p.App}")) + (selected?.Start.ToString("O") ?? "");
        if (key == peaksKey) return;
        peaksKey = key;
        peaksList.Children.Clear();
        if (shownPeaks.Count == 0)
        {
            peaksList.Children.Add(Label(L("No peaks in this period"), 12));
            return;
        }
        bool prefix = metric == Metric.Cpu;
        foreach (var p in shownPeaks)
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var time = Num($"{p.Start:HH:mm}", "FuseboxOnRow");
            var len = Num(PeakInfo.Length(p.Seconds), "FuseboxOnRowSecondary");
            var val = Num(PeakInfo.RowValue(p, prefix), "FuseboxOnRow");
            var cause = Label(PeakInfo.CauseText(p) ?? "–", 12);
            cause.HorizontalAlignment = HorizontalAlignment.Right; cause.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(len, 1); Grid.SetColumn(val, 2); Grid.SetColumn(cause, 3);
            g.Children.Add(time); g.Children.Add(len); g.Children.Add(val); g.Children.Add(cause);
            var btn = new Border
            {
                Child = g, Padding = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(4),
                Background = p == selected ? B("FuseboxAccentSoft") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            ToolTipService.SetToolTip(btn, L("Show in the graph"));
            var peak = p;
            btn.Tapped += (_, _) => Select(peak);
            peaksList.Children.Add(btn);
        }
    }

    static TextBlock Num(string text, string brush, double pt = 12)
    {
        var t = new TextBlock { Text = text, Foreground = B(brush), TextWrapping = TextWrapping.NoWrap };
        Look.Number(t, pt);
        return t;
    }

    void RenderCores(LiveSample s)
    {
        bool has = metric == Metric.Cpu && s.Cores.Length > 0;
        coresCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;
        var hw = App.Live.Hardware;
        coresTitle.Text = hw.EfficiencyCores > 0
            ? L("Per core · %lld efficiency + %lld performance", hw.EfficiencyCores, hw.PerformanceCores) : L("Per core · %lld cores", s.Cores.Length);
        coreBars.Set(s.Cores, hw.EfficiencyLogical);
        // a % label under each bar while the bars are wide enough (≥ 18 DIPs), in the same order (E-cores first)
        var order = Enumerable.Range(0, s.Cores.Length).OrderBy(i => i < hw.EfficiencyLogical.Length && hw.EfficiencyLogical[i] ? 0 : 1).ToList();
        bool labels = coreBars.ActualWidth / s.Cores.Length >= 18;
        coreLabels.Visibility = labels ? Visibility.Visible : Visibility.Collapsed;
        if (!labels) return;
        if (coreLabels.Children.Count != order.Count)
        {
            coreLabels.Children.Clear(); coreLabels.ColumnDefinitions.Clear();
            for (int k = 0; k < order.Count; k++)
            {
                coreLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var t = Num("", "FuseboxOnRowSecondary", 9);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(t, k);
                coreLabels.Children.Add(t);
            }
        }
        for (int k = 0; k < order.Count; k++) ((TextBlock)coreLabels.Children[k]).Text = ((long)Math.Round(s.Cores[order[k]])).ToString();
    }

    /// <summary>Label … value rows (facts: 12 / 13 number, top apps: name 12 / value 12 secondary). Rebuilt only when the row count changes.</summary>
    sealed class RowList(bool top)
    {
        public readonly StackPanel Panel = new() { Spacing = top ? 5 : 7 };
        readonly List<(TextBlock L, TextBlock V)> rows = [];

        public void Set(IReadOnlyList<Fact> list)
        {
            while (rows.Count > list.Count) { Panel.Children.RemoveAt(rows.Count - 1); rows.RemoveAt(rows.Count - 1); }
            while (rows.Count < list.Count)
            {
                var g = new Grid { ColumnSpacing = 8 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var l = new TextBlock { Foreground = B(top ? "FuseboxOnRow" : "FuseboxOnRowSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                var v = new TextBlock { Foreground = B(top ? "FuseboxOnRowSecondary" : "FuseboxOnRow"), HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                Look.Label(l, 12); Look.Number(v, top ? 12 : 13);
                Grid.SetColumn(v, 1);
                g.Children.Add(l); g.Children.Add(v);
                Panel.Children.Add(g);
                rows.Add((l, v));
            }
            for (int i = 0; i < list.Count; i++) { rows[i].L.Text = list[i].Label; rows[i].V.Text = list[i].Value; }
        }

        public void SetText(string text)
        {
            Set([new(text, "")]);
        }
    }
}
