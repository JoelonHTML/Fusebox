using Fusebox.Core;
using Fusebox.Core.Live;
using Fusebox.Core.Look;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Controls;

/// <summary>What LivePage hands a card on every render.</summary>
public sealed class CardFrame
{
    public Tier Tier;
    public bool Graph;                       // M and up, and pref graphs ≠ never
    public bool PerCore;                     // pref perCore (cores are always shown at L and up)
    public CardData Data = CardData.Empty;
    public IReadOnlyList<Fact> Facts = [];
    public IReadOnlyList<double> Main = [];
    public IReadOnlyList<double>? Second;
    public double Top = 100;
    public bool Glide;
    public Sparkline.HoverText? Hover;
    public IReadOnlyList<PeakSpan> Spans = [];
    public Peak? RecentPeak;                 // the dot on XS/S
    public LiveSample? Sample;               // cores for the CPU bars
}

/// <summary>
/// One Live card (§3.3) in the theme's row style: label · peak dot · side icon · side text · core bars · spacer · value box · unit, then
/// facts (L/XL) and the graph. Identity stays per metric, so a layout change never shows another metric's value. Updated only by
/// LivePage.Render. Click = detail screen; a peak dot or a graph peak opens it with that peak selected.
/// </summary>
public sealed partial class MetricCard : UserControl   // Border is sealed in WinUI
{
    public Metric Metric { get; }
    /// <summary>Card click (the peak is null) or a peak dot / graph peak click.</summary>
    public event Action<Metric, Peak?>? Clicked;
    public event Action<string>? AppClicked;

    int index;
    readonly Border row = new();
    readonly Grid body = new() { RowSpacing = 2 };
    readonly Grid header = new() { ColumnSpacing = 6 };
    readonly StackPanel left = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock label = new() { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    readonly Ellipse dot = new() { Width = 6, Height = 6, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    readonly FontIcon warn = new() { Glyph = "", Visibility = Visibility.Collapsed, FontSize = 12 };
    readonly FontIcon sideIcon = new() { FontSize = 13, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    readonly ChangingText side;
    readonly CoreBars cores = new() { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    readonly ChangingText value;
    readonly TextBlock unit = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.NoWrap };
    readonly Viewbox valueBox = new() { StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly Grid facts = new() { ColumnSpacing = 28, RowSpacing = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 6) };
    readonly Sparkline spark = new() { MinHeight = 16, Visibility = Visibility.Collapsed };
    readonly List<(TextBlock Label, TextBlock Value, Border Hit)> factCells = [];
    Tier tier = Tier.S;
    Peak? dotPeak;
    int factCols = 1, factCount;
    bool revealed;

    public MetricCard(Metric m, int index)
    {
        Metric = m;
        this.index = index;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        side = new ChangingText(13) { Index = index };
        value = new ChangingText(30) { Index = index };
        label.Text = Metrics.Label(m);

        left.Children.Add(label); left.Children.Add(dot); left.Children.Add(warn); left.Children.Add(sideIcon); left.Children.Add(side); left.Children.Add(cores);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var vs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        vs.Children.Add(value); vs.Children.Add(unit);
        valueBox.Child = vs;
        Grid.SetColumn(valueBox, 1);
        header.Children.Add(left); header.Children.Add(valueBox);

        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(facts, 1); Grid.SetRow(spark, 2);
        body.Children.Add(header); body.Children.Add(facts); body.Children.Add(spark);
        row.Child = body;
        Content = row;

        ToolTipService.SetToolTip(row, L("Click for details"));
        row.Tapped += (_, _) => Clicked?.Invoke(Metric, null);
        dot.Tapped += (_, e) => { e.Handled = true; Clicked?.Invoke(Metric, dotPeak); };
        spark.PeakClicked += (span, pos) => { if (span.Tag is Peak pk) PeakPopover.Show(spark, pos, pk); };
        row.SizeChanged += (_, _) => Columns();
        Loaded += (_, _) => { ThemeService.ThemeChanged += Restyle; Restyle(); };
        Unloaded += (_, _) => ThemeService.ThemeChanged -= Restyle;
        Restyle();
    }

    /// <summary>Position in the grid: picks the block colour in Color blocks and the number stagger.</summary>
    public void SetIndex(int i) { index = i; side.Index = i; value.Index = i; ThemedRow.Apply(row, i); ApplyTier(tier, force: true); }

    static Brush B(string key) => ThemeService.Brush(key);

    /// <summary>Brush for a level (blocks: always white; colours can't signal warnings on a coloured block).</summary>
    static Brush LevelBrush(int level) =>
        ThemeService.Current.RowStyle == RowStyle.Blocks || level == 0 ? B("FuseboxOnRow") : B(level >= 2 ? "FuseboxDanger" : "FuseboxWarn");

    void Restyle()
    {
        ThemedRow.Apply(row, index);
        ApplyTier(tier, force: true);
        bool blocks = ThemeService.Current.RowStyle == RowStyle.Blocks;
        dot.Fill = B(blocks ? "FuseboxOnRow" : "FuseboxWarn");
        warn.Foreground = B("FuseboxOnRow");
        label.Foreground = B("FuseboxLabel");
        unit.Foreground = B("FuseboxOnRowSecondary");
    }

    /// <summary>Sizes per tier (§3.2/§3.3): XS 11/15, everything else 13/30·24; S header min 36; padding reduced at XS.</summary>
    void ApplyTier(Tier t, bool force = false)
    {
        if (t == tier && !force) return;
        tier = t;
        bool xs = t == Tier.XS;
        Look.SetLabel(label, xs ? 11 : 13);
        Look.SetNumber(unit, xs ? 11 : 14);
        sideIcon.FontSize = xs ? 11 : 13;
        var spec = RowSpec.For(ThemeService.Current.RowStyle);
        double inner = spec.RowPad - (spec.PadH == 0 ? 0 : 6);
        double padV = xs ? Math.Max(spec.PadV - inner / 4, 1) : spec.PadV;
        row.Padding = new Thickness(spec.PadH, padV, spec.PadH, padV);
        header.MinHeight = t == Tier.S ? 36 : 0;
        body.VerticalAlignment = t <= Tier.S ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        spark.BandHeight = t >= Tier.L ? 20 : 0;
        spark.Full = t >= Tier.L;
        spark.Margin = new Thickness(0, 2, 0, 0);
    }

    /// <summary>Everything changes here and only here; called from LivePage.Render (watched, visible).</summary>
    public void Update(CardFrame f)
    {
        ApplyTier(f.Tier);
        var d = f.Data;
        bool xs = f.Tier == Tier.XS;
        double vs = LiveCards.ValueSize(Metric, xs);
        foreach (var t in value.Children.OfType<TextBlock>()) Look.SetNumber(t, vs);
        value.Template = d.Template;
        value.Set(d.Value);
        value.Foreground = LevelBrush(d.Level);
        unit.Text = d.Unit;
        unit.Visibility = d.Unit.Length > 0 && d.Value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool blocks = ThemeService.Current.RowStyle == RowStyle.Blocks;
        warn.Visibility = blocks && d.Level > 0 ? Visibility.Visible : Visibility.Collapsed;

        sideIcon.Glyph = d.SideIcon;
        sideIcon.Visibility = d.SideIcon.Length > 0 && !(Metric == Metric.Battery && double.IsNaN(f.Sample?.Battery ?? double.NaN)) ? Visibility.Visible : Visibility.Collapsed;
        sideIcon.Foreground = d.SideLevel > 0 ? LevelBrush(d.SideLevel) : B("FuseboxOnRowSecondary");
        foreach (var t in side.Children.OfType<TextBlock>()) Look.SetNumber(t, xs ? 11 : 13);
        side.Template = d.Side;
        side.Foreground = d.SideLevel > 0 ? LevelBrush(d.SideLevel) : B("FuseboxOnRowSecondary");
        side.Set(d.Side);
        side.Visibility = d.Side.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // peak dot (XS/S): a peak in the last 5 minutes
        dotPeak = f.Tier <= Tier.S ? f.RecentPeak : null;
        dot.Visibility = dotPeak != null ? Visibility.Visible : Visibility.Collapsed;
        if (dotPeak != null) ToolTipService.SetToolTip(dot, PeakInfo.Label(dotPeak));

        // per-core bars on CPU: always at L and up, or pref perCore
        bool bars = Metric == Metric.Cpu && f.Sample is { Cores.Length: > 0 } && (f.Tier >= Tier.L || f.PerCore);
        cores.Visibility = bars ? Visibility.Visible : Visibility.Collapsed;
        if (bars)
        {
            var hw = App.Live.Hardware;
            cores.Configure(false, f.Tier >= Tier.L ? 6 : 4, f.Tier >= Tier.L ? 18 : f.Tier == Tier.XS ? 10 : 14);
            cores.Set(f.Sample!.Cores, hw.EfficiencyLogical);
            ToolTipService.SetToolTip(cores, hw.EfficiencyCores > 0
                ? L("Per core: %lld efficiency + %lld performance", hw.EfficiencyCores, hw.PerformanceCores) : L("Per core · %lld cores", f.Sample.Cores.Length));
        }

        // facts (L/XL)
        bool showFacts = f.Tier >= Tier.L && f.Facts.Count > 0;
        facts.Visibility = showFacts ? Visibility.Visible : Visibility.Collapsed;
        if (showFacts) RenderFacts(f.Facts.Take(f.Tier == Tier.XL ? 9 : 4).ToList(), f.Tier == Tier.XL);

        // graph (M and up)
        bool g = f.Tier >= Tier.M && f.Graph;
        spark.Visibility = g ? Visibility.Visible : Visibility.Collapsed;
        if (g)
        {
            spark.Glide = f.Glide;
            spark.SetHover(f.Hover);
            spark.SetPeaks(f.Tier >= Tier.L ? f.Spans : []);
            spark.Update(f.Main, f.Second, f.Top);
            if (!revealed)
            {
                revealed = true;
                if (Anim.IntroActive) spark.Reveal(index, Anim.Pace);
            }
        }
    }

    void RenderFacts(IReadOnlyList<Fact> list, bool xl)
    {
        facts.RowSpacing = xl ? 8 : 6;
        if (list.Count != factCount) { factCount = list.Count; Rebuild(list.Count, xl); }
        for (int i = 0; i < list.Count; i++)
        {
            factCells[i].Label.Text = list[i].Label;
            factCells[i].Value.Text = list[i].Value;
            bool app = list[i].App != null;
            factCells[i].Hit.Tag = list[i].App;
            ToolTipService.SetToolTip(factCells[i].Hit, app ? L("Open %@", list[i].App!) : null);
        }
    }

    void Columns()
    {
        int c = Math.Max((int)Math.Floor((row.ActualWidth - row.Padding.Left - row.Padding.Right + 28) / (230 + 28)), 1);
        if (c == factCols) return;
        factCols = c;
        if (factCount > 0) Rebuild(factCount, tier == Tier.XL);
    }

    void Rebuild(int n, bool xl)
    {
        facts.Children.Clear(); facts.ColumnDefinitions.Clear(); facts.RowDefinitions.Clear(); factCells.Clear();
        int cols = Math.Min(factCols, Math.Max(n, 1));
        for (int c = 0; c < cols; c++) facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (n + cols - 1) / cols;
        for (int r = 0; r < rows; r++) facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < n; i++)
        {
            var lab = new TextBlock { Foreground = B("FuseboxOnRowSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            var val = new TextBlock { Foreground = B("FuseboxOnRow"), TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Right };
            Look.Label(lab, xl ? 13.5 : 12.5); Look.Number(val, xl ? 13.5 : 12.5);
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(val, 1);
            g.Children.Add(lab); g.Children.Add(val);
            var hit = new Border { Child = g, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            hit.Tapped += (_, e) => { if (hit.Tag is string app) { e.Handled = true; AppClicked?.Invoke(app); } };
            Grid.SetRow(hit, i / cols); Grid.SetColumn(hit, i % cols);
            facts.Children.Add(hit);
            factCells.Add((lab, val, hit));
        }
    }
}
