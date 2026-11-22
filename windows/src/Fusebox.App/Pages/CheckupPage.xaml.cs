using Fusebox.App.Controls;
using Fusebox.Core;
using Fusebox.Core.Checkup;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>
/// Checkup (§5): period picker, health score 1–10, five collapsible groups (Fluent Expanders, content built only when opened), tips,
/// "Why is my PC slow?", cards, export bar. Reads the CSV logs and Windows' own history off the UI thread when the page opens, the period
/// changes or Reload is pressed; the cached report shows instantly first. Nothing here runs on a timer.
/// </summary>
public sealed partial class CheckupPage : Page
{
    sealed class Grp
    {
        public string Id = "";
        public Expander Exp = null!;
        public TextBlock Summary = null!;
        public bool Built;
    }

    static readonly string[] Order = ["perf", "power", "stability", "storage", "history"];

    Period period = Period.Today;
    CheckupBundle? bundle;
    CancellationTokenSource? cts;
    bool visible, suppress, groupsDirty = true;
    HashSet<string> open = [];
    readonly Dictionary<string, Grp> groups = [];

    public CheckupPage()
    {
        InitializeComponent();
        foreach (var p in new[] { Period.Today, Period.Yesterday, Period.Week })
            PeriodBar.Items.Add(new SelectorBarItem { Text = Periods.Title(p), Tag = Periods.Id(p) });
        ToolTipService.SetToolTip(ReloadBtn, L("Reload"));
        AutomationProperties.SetName(ReloadBtn, L("Reload"));
    }

    bool HasBattery => App.Live.Last is { } l && double.IsFinite(l.Battery);

    // ---- lifecycle ----

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        visible = true;
        var start = Prefs.Str("checkupStart");
        period = e.Parameter is string pid ? Periods.Parse(pid, Period.Today)
            : Periods.Parse(start == "last" ? Prefs.Str("analysisPeriod") : start, Period.Today);
        suppress = true;
        PeriodBar.SelectedItem = PeriodBar.Items.First(i => (string)i.Tag == Periods.Id(period));
        suppress = false;
        open = LoadOpen();
        ThemeService.ThemeChanged += OnTheme;
        bundle = CheckupData.Cached(period);
        Render();
        _ = ReloadAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        visible = false;
        cts?.Cancel();
        DiskCard.Cancel();       // leaving the page cancels the disk scan and the speed test (the temp file is deleted)
        SsdSpeedCard.Cancel();
        ThemeService.ThemeChanged -= OnTheme;
    }

    void OnTheme() { groupsDirty = true; if (visible) Render(); }

    static HashSet<string> LoadOpen()
    {
        // Default: only Performance. "-" = the user closed every group.
        if (!Prefs.Store.IsSet("checkupOpen")) return ["perf"];
        return [.. Prefs.List("checkupOpen").Where(Order.Contains)];
    }

    void SaveOpen() => Prefs.Set("checkupOpen", open.Count == 0 ? "-" : string.Join(',', Order.Where(open.Contains)));

    // ---- period and reload ----

    void OnPeriod(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (suppress || sender.SelectedItem?.Tag is not string id) return;
        period = Periods.Parse(id, Period.Today);
        Prefs.Set("analysisPeriod", id);
        bundle = CheckupData.Cached(period);   // the cached report shows at once, then the reload replaces it
        groupsDirty = bundle == null || groupsDirty;
        Render();
        _ = ReloadAsync();
    }

    void OnReload(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    async Task ReloadAsync()
    {
        cts?.Cancel();
        var mine = cts = new();
        var p = period;
        ReloadBtn.IsEnabled = false; ReloadBtn.Opacity = 0.4;
        try
        {
            var b = await CheckupData.LoadAsync(p, mine.Token);
            if (mine.IsCancellationRequested || !visible || p != period) return;
            bundle = b;
            Render();
        }
        catch (OperationCanceledException) { }
        finally { if (!mine.IsCancellationRequested) { ReloadBtn.IsEnabled = true; ReloadBtn.Opacity = 1; } }
    }

    // ---- rendering ----

    CardCtx NewCtx() => new()
    {
        R = bundle!.Report, Tips = bundle.Tips, Root = XamlRoot, Alive = () => visible, Width = Math.Max(ActualWidth - 32, 0), Summaries = RefreshSummaries,
        Hide = [.. Prefs.List("checkupHide")], HasBattery = HasBattery,
    };

    void Render()
    {
        ScoreHost.Children.Clear();
        ExportHost.Children.Clear();
        if (bundle == null)
        {
            // First open: the "Why is my PC slow?" card is usable at once, with a small spinner underneath.
            GroupsHost.Children.Clear(); groups.Clear(); groupsDirty = true;
            ScoreHost.Children.Add(CheckupCards.Why(new CardCtx { Root = XamlRoot, Alive = () => visible, Width = Math.Max(ActualWidth - 32, 0) }));
            var ring = Ui.Spinner(); ring.HorizontalAlignment = HorizontalAlignment.Center; ring.Margin = new Thickness(0, 20, 0, 20);
            ScoreHost.Children.Add(ring);
            return;
        }
        var ctx = NewCtx();
        if (ctx.Show("score")) ScoreHost.Children.Add(ScoreCard(bundle.Report));
        if (groupsDirty) { BuildGroups(ctx); groupsDirty = false; }
        else foreach (var g in groups.Values.Where(g => g.Exp.IsExpanded)) Fill(g, ctx);
        RefreshSummaries();
        if (!groups.ContainsKey("history")) ExportHost.Children.Add(ExportBar());
    }

    UIElement ScoreCard(Report r)
    {
        var level = HealthScore.Level(r.Score);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var num = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var color = level is { } lv ? Ui.Tone(lv == 0 ? 3 : lv == 1 ? 1 : 2, false) : Ui.OnRow(true);
        var score = Ui.Num(r.Score?.ToString() ?? "–", 40, color: Ui.Blocks ? Ui.OnRow() : color);
        num.Children.Add(score);
        var of = Ui.Num("/10", 14, secondary: true); of.VerticalAlignment = VerticalAlignment.Bottom; of.Margin = new Thickness(0, 0, 0, 8);
        num.Children.Add(of);
        if (r.Score != null) grid.Children.Add(num);   // no "– /10" placeholder: the text says why there is no score yet
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Label(L("Health"), 14, bold: true));
        text.Children.Add(Ui.Label(r.Reason, 12, secondary: true));
        var cmp = HealthScore.Compare(r.Score, r.PrevScore, Periods.PreviousName(r.Period));
        if (cmp.Length > 0) text.Children.Add(Ui.Label(cmp, 11, secondary: true));
        if (r.Minutes < 60) text.Children.Add(Ui.Label(L("Fusebox has only just started measuring. Windows' own history fills in Stability and Battery."), 11, secondary: true));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return Ui.Card(0, grid);
    }

    // ---- groups (§5.2, §5.8) ----

    bool Visible(string id, CardCtx c) => id switch
    {
        "perf" => new[] { "tops", "busy", "peaks", "background" }.Any(c.Show),
        "power" => new[] { "battery", "drain", "sleep", "awake", "health" }.Any(c.Show) && (c.HasBattery || c.Show("awake")),
        "stability" => c.Show("crashes"),
        "storage" => new[] { "startup", "disk", "ssdBench" }.Any(c.Show),
        _ => c.Show("runtime") || c.Show("log"),
    };

    (string Title, string Glyph, bool Warn, string Summary) Info(string id, CardCtx c)
    {
        var r = c.R;
        int tips = c.Tips.Count(t => t.Group == (id == "perf" ? "perf" : id));
        string Join(IEnumerable<string> parts) => string.Join(" · ", (tips > 0 && c.Show("tips") ? [tips == 1 ? L("1 tip") : L("%lld tips", tips)] : Array.Empty<string>()).Concat(parts));
        switch (id)
        {
            case "perf":
                var busiest = r.TopCpu.FirstOrDefault(a => a.Name != "Windows");
                return (L("Performance"), "", r.Busy.Count > 0, Join(new[]
                {
                    busiest != null ? L("%@ busiest", busiest.Name) : L("No app data yet"),
                    r.Busy.Count == 0 ? "" : r.Busy.Count == 1 ? L("1 busy moment") : L("%lld busy moments", r.Busy.Count),
                    r.Peaks.Count == 0 ? L("no peaks") : r.Peaks.Count == 1 ? L("1 peak") : L("%lld peaks", r.Peaks.Count),
                }.Where(s => s.Length > 0)));
            case "power":
            {
                var parts = new List<string>();
                if (SleepCard.LastLoss is { } loss) parts.Add(L("lost %lld%% while asleep", loss));
                if (BatteryHealthCard.Percent is { } p) parts.Add(L("%lld%% capacity", p));
                if (BatteryHealthCard.Cycles is { } cy) parts.Add(L("%lld cycles", cy));
                if (parts.Count == 0) parts.Add(L("What uses energy and what keeps your PC awake"));
                return (c.HasBattery ? L("Battery & power") : L("Power"), c.HasBattery ? "" : "", BatteryHealthCard.Worn, Join(parts));
            }
            case "stability":
            {
                var k = r.Crashes;
                var parts = new List<string>();
                if (k.Crashed > 0) parts.Add(k.Crashed == 1 ? L("1 app crashed") : L("%lld apps crashed", k.Crashed));
                if (k.Hung > 0) parts.Add(L("%lld stopped responding", k.Hung));
                if (k.OutOfMemory > 0) parts.Add(L("out of memory %lld×", k.OutOfMemory));
                if (parts.Count == 0) parts.Add(L("No crashes or hangs %@", r.When));
                return (L("Stability"), "", k.Apps.Count > 0 || k.BlueScreens > 0, Join(parts));
            }
            case "storage":
            {
                var parts = new List<string>();
                if (StartupCard.Count is { } n) parts.Add(L("%lld items start at login", n));
                if (StartupCard.Orphans > 0) parts.Add(L("%lld left behind", StartupCard.Orphans));
                parts.Add(L("%lld GB free", (long)DiskCard.FreeGb));
                return (L("Startup & storage"), "", StartupCard.Orphans > 0 || DiskCard.Low, Join(parts));
            }
            default:
                return (L("History & export"), "", false, Join([L("Measured %@ · %@", CheckupFmt.Duration(r.Minutes),
                    r.Events.Count == 0 ? L("nothing unusual") : r.Events.Count == 1 ? L("1 event") : L("%lld events", r.Events.Count))]));
        }
    }

    void BuildGroups(CardCtx c)
    {
        GroupsHost.Children.Clear();
        groups.Clear();
        foreach (var id in Order)
        {
            if (!Visible(id, c)) continue;
            var (title, glyph, warn, summary) = Info(id, c);
            var g = new Grp { Id = id };
            var head = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 10, 0, 10) };
            head.ColumnDefinitions.Add(new() { Width = new GridLength(22) });
            head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new FontIcon { Glyph = glyph, FontSize = Ui.T.Size(15), VerticalAlignment = VerticalAlignment.Center, Foreground = warn ? Ui.B("FuseboxWarn") : Ui.Blocks ? Ui.B("FuseboxText") : Ui.B("FuseboxAccent") };
            head.Children.Add(icon);
            var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            var t = new TextBlock { Text = title, Foreground = Ui.B("FuseboxText"), FontWeight = FontWeights.Bold };
            Look.Label(t, 15); t.FontWeight = FontWeights.Bold;
            g.Summary = new TextBlock { Text = summary, Foreground = Ui.B(warn ? "FuseboxWarn" : "FuseboxSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
            Look.Label(g.Summary, 11);
            texts.Children.Add(t); texts.Children.Add(g.Summary);
            Grid.SetColumn(texts, 1);
            head.Children.Add(texts);
            g.Exp = new Expander
            {
                Header = head, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderBrush = Ui.B("FuseboxLine"), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0), Padding = new Thickness(0),
            };
            g.Exp.IsExpanded = open.Contains(id);
            AutomationProperties.SetName(g.Exp, $"{title}, {(g.Exp.IsExpanded ? L("expanded") : L("collapsed"))}");
            var gid = id;
            g.Exp.Expanding += (_, _) => { open.Add(gid); SaveOpen(); AutomationProperties.SetName(g.Exp, $"{Info(gid, NewCtx()).Title}, {L("expanded")}"); if (!g.Built && bundle != null) Fill(g, NewCtx()); };
            g.Exp.Collapsed += (_, _) => { open.Remove(gid); SaveOpen(); g.Exp.Content = null; g.Built = false; AutomationProperties.SetName(g.Exp, $"{Info(gid, NewCtx()).Title}, {L("collapsed")}"); };
            groups[id] = g;
            GroupsHost.Children.Add(g.Exp);
            if (g.Exp.IsExpanded) Fill(g, c);
        }
    }

    /// <summary>Builds the content of an open group (tips first, then the cards). Collapsed groups are never built.</summary>
    void Fill(Grp g, CardCtx c)
    {
        var s = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 12) };
        if (CheckupCards.TipsList(c, g.Id) is { } tips) s.Children.Add(tips);
        double w = c.Width;
        switch (g.Id)
        {
            case "perf":
                s.Children.Add(CheckupCards.Why(c));
                if (c.Show("tops") && (c.R.TopCpu.Count > 0 || c.R.TopRam.Count > 0))   // empty Top 5 cards stay hidden
                {
                    var tops = new TwoUp(388, w, 8);
                    tops.Left.Children.Add(CheckupCards.Top(c, true)); tops.Right.Children.Add(CheckupCards.Top(c, false));
                    s.Children.Add(tops);
                }
                var cols = new TwoUp(588, w);
                if (c.Show("peaks")) cols.Left.Children.Add(CheckupCards.Peaks(c));
                if (c.Show("busy")) cols.Right.Children.Add(CheckupCards.Busy(c));
                if (c.Show("background")) cols.Right.Children.Add(CheckupCards.Background(c));
                s.Children.Add(cols.Done());
                break;
            case "power":
            {
                var p = new TwoUp(588, w);
                if (c.Show("health") && c.HasBattery) p.Left.Children.Add(BatteryHealthCard.Build(c));
                else if (c.Show("battery") && c.HasBattery) p.Left.Children.Add(CheckupCards.BatterySimple(c));
                if (c.Show("drain") && c.HasBattery) p.Left.Children.Add(DrainCard.Build(c));
                if (c.Show("sleep") && c.HasBattery) p.Right.Children.Add(SleepCard.Build(c));
                if (c.Show("awake")) p.Right.Children.Add(AwakeCard.Build(c));
                s.Children.Add(p.Done());
                break;
            }
            case "stability":
            {
                var p = new TwoUp(588, w);
                p.Left.Children.Add(CheckupCards.Crashes(c));
                p.Right.Children.Add(CheckupCards.Stability(c));
                s.Children.Add(p.Done());
                break;
            }
            case "storage":
            {
                var p = new TwoUp(588, w);
                if (c.Show("startup")) p.Left.Children.Add(StartupCard.Build(c));
                if (c.Show("disk")) { p.Right.Children.Add(DiskCard.Build(c)); p.Right.Children.Add(SsdHealthCard.Build(c)); }
                if (c.Show("ssdBench")) p.Right.Children.Add(SsdSpeedCard.Build(c));
                s.Children.Add(p.Done());
                break;
            }
            default:
            {
                var p = new TwoUp(588, w);
                if (c.Show("runtime")) p.Left.Children.Add(CheckupCards.Running(c));
                if (c.Show("log")) p.Right.Children.Add(CheckupCards.Log(c));
                s.Children.Add(p.Done());
                s.Children.Add(ExportBar());
                break;
            }
        }
        g.Exp.Content = s;
        g.Built = true;
    }

    /// <summary>Header summaries from what is already known (report and caches); nothing is loaded for them.</summary>
    void RefreshSummaries()
    {
        if (bundle == null || !visible) return;
        var c = NewCtx();
        foreach (var g in groups.Values)
        {
            var (_, _, warn, summary) = Info(g.Id, c);
            g.Summary.Text = summary;
            g.Summary.Foreground = Ui.B(warn ? "FuseboxWarn" : "FuseboxSecondary");
        }
    }

    // ---- export bar (§5.9) ----

    UIElement ExportBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        bar.Children.Add(Ui.Pill(L("Share report…"), Ui.B("FuseboxSecondary"), () => { if (bundle != null && XamlRoot != null) _ = CheckupShare.ShareAsync(XamlRoot, bundle); }));
        bar.Children.Add(Ui.Pill(L("Export CSV"), Ui.B("FuseboxSecondary"), () => { if (XamlRoot != null) _ = CheckupShare.ExportCsvAsync(XamlRoot, period); }));
        return bar;
    }
}
