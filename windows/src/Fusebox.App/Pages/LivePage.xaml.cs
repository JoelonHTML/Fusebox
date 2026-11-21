using Fusebox.App.Controls;
using Fusebox.Core;
using Fusebox.Core.Live;
using Fusebox.Core.Look;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>
/// The Live tab (§3): the card grid from <see cref="LiveLayout"/> (tiers XS…XL, rows share the height, never scrolls), optional advice line,
/// cards in the user's metric order. Redraws only on LiveHub.Updated while shown; subscribes in OnNavigatedTo. Clicking a card opens
/// <see cref="DetailPage"/>; <c>App.Main.Open("Live", new DetailRequest(metric))</c> opens the detail screen directly.
/// </summary>
public sealed partial class LivePage : Page
{
    readonly Dictionary<Metric, MetricCard> cards = [];
    List<Metric> shown = [];
    LiveLayout layout;
    string layoutKey = "";
    bool introPending;
    readonly Microsoft.UI.Dispatching.DispatcherQueue ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

    public LivePage()
    {
        InitializeComponent();
        PeakAlert.EnsureStarted();   // per-minute sustained-peak notification + peak snapshots (App.xaml.cs may call it too; it is idempotent)
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is DetailRequest or Metric)
        {
            // Open("Live", metric): straight to the detail screen (back = this page).
            var req = e.Parameter as DetailRequest ?? new DetailRequest((Metric)e.Parameter);
            ui.TryEnqueue(() => Frame.Navigate(typeof(DetailPage), req));
            return;
        }
        if (e.NavigationMode != NavigationMode.Back) { Anim.BeginIntro(); introPending = true; }
        App.Live.Updated += Render;
        Prefs.Changed += OnPref;
        ThemeService.ThemeChanged += OnTheme;
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Live.Updated -= Render;
        Prefs.Changed -= OnPref;
        ThemeService.ThemeChanged -= OnTheme;
    }

    static readonly HashSet<string> Watching =
        ["metrics", "absolute", "graphs", "shortMinutes", "perCore", "showAdvice", "netBits", "netPing", "pingHost", "batteryTime", "numberStyle", "smoothBig", "warnCPU", "warnTemp", "warnPing"];

    void OnPref(string key) { if (Watching.Contains(key)) ui.TryEnqueue(() => { layoutKey = ""; Render(); }); }
    void OnTheme() { layoutKey = ""; Render(); }

    void Root_SizeChanged(object sender, SizeChangedEventArgs e) { if (Arrange()) Render(); }

    /// <summary>Recomputes the grid for the current size; true when it changed (the cards need new content).</summary>
    bool Arrange()
    {
        var s = App.Live.Last;
        var want = MetricText.Enabled(s);
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w <= 0 || h <= 0) return false;
        var theme = ThemeService.Current;
        bool advice = Prefs.Bool("showAdvice");
        var l = LiveLayout.Compute(w, h, want.Count, theme.RowPad, advice);
        string key = $"{l}|{string.Join(',', want)}|{theme.RowStyle}|{advice}";
        if (key == layoutKey) return false;
        layoutKey = key; layout = l; shown = want;

        foreach (var c in cards.Values) if (c.Parent is Panel p) p.Children.Remove(c);
        Cards.Children.Clear(); Cards.RowDefinitions.Clear(); Cards.ColumnDefinitions.Clear();
        bool lines = theme.RowStyle is RowStyle.Lines or RowStyle.Plain;
        Cards.RowSpacing = lines ? 0 : l.Tier == Tier.XS ? 3 : 6;
        Cards.ColumnSpacing = 10;
        for (int c = 0; c < l.Cols; c++) Cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < l.Rows; r++) Cards.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < want.Count; i++)
        {
            if (!cards.TryGetValue(want[i], out var card)) cards[want[i]] = Make(want[i], i);
            else card.SetIndex(i);
        }
        // Rows: a short last row in XS/S keeps its columns (invisible filler); in M and up its cards are wider.
        for (int r = 0; r < l.Rows; r++)
        {
            int first = r * l.Cols, count = Math.Min(l.Cols, want.Count - first);
            if (count <= 0) break;
            if (count == l.Cols || l.Tier <= Tier.S)
            {
                for (int k = 0; k < count; k++) Place(cards[want[first + k]], r, k, 1);
            }
            else
            {
                var sub = new Grid { ColumnSpacing = 10 };
                for (int k = 0; k < count; k++)
                {
                    sub.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var c = cards[want[first + k]];
                    Grid.SetColumn(c, k);
                    sub.Children.Add(c);
                }
                Grid.SetRow(sub, r); Grid.SetColumnSpan(sub, l.Cols);
                Cards.Children.Add(sub);
            }
        }
        if (want.Count == 0)
        {
            // Nothing switched on: say where to change it instead of an empty page.
            var hint = new TextBlock { Text = L("Nothing is switched on. Choose what to show in Settings › Live."), TextWrapping = TextWrapping.Wrap, Foreground = ThemeService.Brush("FuseboxSecondary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            Look.Label(hint, 13);
            Cards.Children.Add(hint);
        }
        Advice.Visibility = advice && l.Tier > Tier.XS ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    MetricCard Make(Metric m, int index)
    {
        var c = new MetricCard(m, index);
        c.Clicked += (metric, peak) => Frame.Navigate(typeof(DetailPage), new DetailRequest(metric, peak?.Start));
        c.AppClicked += name =>
        {
            var key = App.Live.Apps?.FirstOrDefault(g => g.Name == name)?.Key;
            if (key != null) App.Main?.Open("Processes", key);
        };
        return c;
    }

    void Place(MetricCard c, int row, int col, int span)
    {
        Grid.SetRow(c, row); Grid.SetColumn(c, col); Grid.SetColumnSpan(c, span);
        Cards.Children.Add(c);
    }

    void Render()
    {
        Arrange();
        if (shown.Count == 0) return;
        var s = App.Live.Last;
        var o = MetricText.Options();
        bool graphs = Prefs.Str("graphs") != "never";
        bool smooth = Prefs.Bool("smoothBig");
        var tier = layout.Tier;
        var list = s == null ? [] : App.Live.History.Range(TimeRange.Short, LiveHub.ShortMinutes, LiveHub.StepSeconds);
        var today = App.Live.Peaks.Today;
        for (int i = 0; i < shown.Count; i++)
        {
            var m = shown[i];
            var f = new CardFrame { Tier = tier, Graph = graphs && tier >= Tier.M, PerCore = Prefs.Bool("perCore"), Sample = s };
            if (s != null)
            {
                f.Data = LiveCards.Card(m, s, o);
                f.RecentPeak = PeakInfo.Recent(m, today, DateTime.Now);
                if (f.Graph)
                {
                    var (main, second) = LiveCards.Series(m, list, o);
                    f.Main = main; f.Second = second; f.Top = LiveCards.GraphTop(m, s, o);
                    f.Glide = tier < Tier.L || smooth;
                    f.Hover = idx => MetricText.Point(m, list, idx, o, today);
                    if (tier >= Tier.L) f.Spans = Spans(m, list, today);
                }
                if (tier >= Tier.L) f.Facts = LiveCards.Facts(m, s, o, f.Main.Count > 0 ? f.Main : LiveCards.Series(m, list, o).Main, App.Live.Apps, today);
            }
            cards[m].Update(f);
        }
        if (Advice.Visibility == Visibility.Visible)
        {
            var (text, level) = App.Live.Status;
            AdviceText.Text = text;
            AdviceDot.Fill = new SolidColorBrush(level >= 2 ? ColorHelper.FromArgb(255, 255, 69, 58) : level == 1 ? ColorHelper.FromArgb(255, 255, 159, 10) : ColorHelper.FromArgb(255, 48, 209, 88));
            Look.Label(AdviceText, 12);
        }
        if (introPending && s != null) { introPending = false; Intro(); }
    }

    /// <summary>Peaks overlapping the shown window, as sample index spans (for the graph's click targets).</summary>
    static List<PeakSpan> Spans(Metric m, IReadOnlyList<LiveSample> list, IReadOnlyList<Peak> today)
    {
        var l = new List<PeakSpan>();
        if (list.Count < 2) return l;
        var kinds = LiveCards.KindsOf(m);
        foreach (var p in today.Where(p => kinds.Contains(p.Kind) && p.End >= list[0].Time && p.Start <= list[^1].Time))
        {
            int a = 0, b = -1;
            while (a < list.Count - 1 && list[a].Time < p.Start) a++;
            for (int i = 0; i < list.Count; i++) if (list[i].Time <= p.End) b = i;
            if (b >= a) l.Add(new(a, Math.Max(b, a + 1), p));
        }
        return l;
    }

    /// <summary>Rows spring in (opacity 0 → 1, 14 DIPs up) within 1.2 s of launch or tab switch; nothing in Low impact.</summary>
    void Intro()
    {
        if (!Anim.IntroActive) return;
        double pace = Anim.Pace;
        for (int i = 0; i < shown.Count; i++)
        {
            var c = cards[shown[i]];
            var move = new TranslateTransform { Y = 14 };
            c.RenderTransform = move;
            c.Opacity = 0;
            Anim.Tween(0.35 * pace, Motion.RowDelay(i, pace), p => { double e = Motion.RevealEase(p); c.Opacity = e; move.Y = 14 * (1 - e); }, () => { c.Opacity = 1; c.RenderTransform = null; });
        }
    }
}
