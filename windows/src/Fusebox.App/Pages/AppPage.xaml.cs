using System.Globalization;
using Fusebox.App.Controls;
using Fusebox.Core;
using Fusebox.Core.Look;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Fusebox.App.Pages;

/// <summary>
/// The app page (FEATURES.md §4.11): header and actions, Now (only while running; graphs collected only while this page is open),
/// History from the apps-*.csv minute log, About. Opened with an <see cref="AppArgs"/> (or a key/name string); Esc or the back button return.
/// </summary>
public sealed partial class AppPage : Page
{
    static readonly Dictionary<string, AppFacts?> FactsCache = new(StringComparer.OrdinalIgnoreCase);   // per exe path, for the session

    AppArgs args = new("");
    AppGroup? app;               // last known state, kept when the app quits
    bool running;
    string? path;
    string name = "";
    History cpuHist = new(300), ramHist = new(300);   // last 300 samples (10 min at 2 s), only while the page is open
    DateTime lastRecorded;
    AppUsage? usage;
    int period = 1;
    long loadTicket;
    bool built;

    // header
    Button back = null!, bring = null!, quit = null!, force = null!, reveal = null!;
    Image icon = null!;
    FontIcon iconFallback = null!;
    TextBlock title = null!, status = null!;
    // now
    StackPanel nowPanel = null!;
    TextBlock cpuNow = null!, memNow = null!, gpuNow = null!, cpuTitleVal = null!, ramTitleVal = null!;
    Grid statGrid = null!;
    Sparkline cpuSpark = null!, ramSpark = null!;
    TextBlock cpuCollecting = null!, ramCollecting = null!;
    StackPanel factsHost = null!;
    // history / about
    readonly Dictionary<int, ToggleButton> periodChips = [];
    StackPanel historyHost = null!, aboutHost = null!;
    readonly List<(Border Card, int Index)> cards = [];

    public AppPage()
    {
        InitializeComponent();
        ProcUi.Flat = (Style)Resources["FlatButton"];
        Loaded += (_, _) => { if (built) return; built = true; Build(); Refill(); };
        Body.SizeChanged += (_, _) => PlaceStatGrids();
    }

    // ---- lifecycle ----

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        args = e.Parameter as AppArgs ?? new AppArgs(e.Parameter as string ?? "");
        path = args.Path;
        name = args.Name ?? args.Key;
        period = Prefs.Str("appPeriod") is "7" ? 7 : Prefs.Str("appPeriod") is "30" ? 30 : 1;
        cpuHist = new(300); ramHist = new(300); lastRecorded = default;
        App.Live.Updated += OnTick;
        ProcessMonitor.Changed += OnChanged;
        ThemeService.ThemeChanged += OnTheme;
        ProcessMonitor.Refresh(true);
        if (built) { Refill(); }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Live.Updated -= OnTick;
        ProcessMonitor.Changed -= OnChanged;
        ThemeService.ThemeChanged -= OnTheme;
        loadTicket++;
    }

    void OnTick() => ProcessMonitor.Refresh();
    void OnChanged() => RenderNow();

    void OnTheme()
    {
        foreach (var (c, i) in cards) ThemedRow.Apply(c, i);
        UpdatePeriodChips();
        RenderHistory();
        RenderNow();
    }

    void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        GoBack();
        args.Handled = true;
    }

    void GoBack() { if (Frame.CanGoBack) Frame.GoBack(); }

    /// <summary>Everything that depends on which app is shown: the header, Now, and the two loads (history, about).</summary>
    void Refill()
    {
        app = null;
        running = false;
        cards.RemoveAll(c => c.Card.Parent == null);
        UpdatePeriodChips();
        RenderNow();
        _ = LoadHistory();
        _ = LoadAbout();
    }

    // ---- build ----

    Border Card(UIElement content, int index = 0, double padding = 10)
    {
        var b = new Border { Child = new Border { Padding = new Thickness(padding), Child = content } };
        ThemedRow.Apply(b, index);
        cards.Add((b, index));
        return b;
    }

    TextBlock Section(string text)
    {
        var t = ProcUi.Text(12, "FuseboxSecondary");
        t.Text = text;
        t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        t.Margin = new Thickness(0, 4, 0, 0);
        return t;
    }

    void Build()
    {
        // header
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        back = new Button { Style = ProcUi.Flat, Width = 26, Height = 26, Content = ProcUi.Glyph("", 13, "FuseboxText") };
        back.Click += (_, _) => GoBack();
        string backTip = Strings.L("Back to Processes (Esc)");
        ToolTipService.SetToolTip(back, backTip);
        AutomationProperties.SetName(back, backTip);
        head.Children.Add(back);
        var (host, img, fb) = ProcUi.IconCell(36);
        icon = img; iconFallback = fb;
        Grid.SetColumn(host, 1);
        head.Children.Add(host);
        var tb = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        title = ProcUi.Text(15, "FuseboxText");
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        status = ProcUi.Text(11, "FuseboxSecondary");
        tb.Children.Add(title);
        tb.Children.Add(status);
        Grid.SetColumn(tb, 2);
        head.Children.Add(tb);
        Body.Children.Add(head);

        // actions, indented 36 (the back button + spacing)
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(36, 0, 0, 0) };
        bring = ProcUi.IconButton("", Strings.L("Bring to front"), "FuseboxAccent");
        bring.Click += (_, _) => BringOrOpen();
        quit = ProcUi.IconButton("", Strings.L("Quit"), "FuseboxAccent");
        quit.Click += async (_, _) => { if (app != null) await StopFlow.RunAsync(XamlRoot, app, StopFlow.Mode.Quit); };
        force = ProcUi.IconButton("", Strings.L("Force quit"), "FuseboxDanger");
        force.Click += async (_, _) => { if (app != null) await StopFlow.RunAsync(XamlRoot, app, StopFlow.Mode.Force); };
        reveal = ProcUi.IconButton("", Strings.L("Show in Explorer"), "FuseboxAccent");
        reveal.Click += (_, _) => { if (path != null) ProcessMonitor.Reveal(path); };
        foreach (var b in new[] { bring, quit, force, reveal }) actions.Children.Add(b);
        Body.Children.Add(actions);

        BuildNow();

        // history
        Body.Children.Add(Section(Strings.L("History")));
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (days, label) in new[] { (1, Strings.L("Today")), (7, Strings.L("7 days")), (30, Strings.L("30 days")) })
        {
            var t = ProcUi.Text(11, "FuseboxSecondary");
            t.Text = label;
            var b = new ToggleButton { Style = (Style)Resources["PeriodChip"], Content = t };
            int d = days;
            b.Click += (_, _) => { period = d; Prefs.Set("appPeriod", d.ToString(CultureInfo.InvariantCulture)); UpdatePeriodChips(); _ = LoadHistory(); };
            periodChips[days] = b;
            chips.Children.Add(b);
        }
        Body.Children.Add(chips);
        historyHost = new StackPanel { Spacing = 8 };
        Body.Children.Add(historyHost);

        // about
        aboutHost = new StackPanel { Spacing = 8 };
        Body.Children.Add(aboutHost);
    }

    void BuildNow()
    {
        nowPanel = new StackPanel { Spacing = 8 };
        nowPanel.Children.Add(Section(Strings.L("Now")));

        statGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8, Tag = 110.0 };
        (cpuNow, var c1) = Stat(Strings.L("CPU"), 0);
        (memNow, var c2) = Stat(Strings.L("Memory"), 1);
        (gpuNow, var c3) = Stat(Strings.L("GPU"), 2);
        foreach (var c in new[] { c1, c2, c3 }) statGrid.Children.Add(c);
        nowPanel.Children.Add(statGrid);

        var sparks = new Grid { ColumnSpacing = 8 };
        sparks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sparks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (var cardA, cpuTitleVal, cpuSpark, cpuCollecting) = SparkCard(Strings.L("CPU"), 0);
        (var cardB, ramTitleVal, ramSpark, ramCollecting) = SparkCard(Strings.L("Memory"), 1);
        Grid.SetColumn(cardB, 1);
        sparks.Children.Add(cardA);
        sparks.Children.Add(cardB);
        nowPanel.Children.Add(sparks);

        factsHost = new StackPanel { Spacing = 7 };
        nowPanel.Children.Add(Card(factsHost, 3));
        Body.Children.Add(nowPanel);
    }

    (TextBlock Value, Border Card) Stat(string label, int index)
    {
        var s = new StackPanel { Spacing = 2 };
        var l = ProcUi.Text(11, "FuseboxOnRowSecondary");
        l.Text = label;
        var v = ProcUi.Text(16, "FuseboxOnRow", number: true);
        s.Children.Add(l);
        s.Children.Add(v);
        return (v, Card(s, index));
    }

    (Border Card, TextBlock Value, Sparkline Spark, TextBlock Collecting) SparkCard(string label, int index)
    {
        var g = new Grid { Height = 70 };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = ProcUi.Text(11, "FuseboxOnRowSecondary");
        l.Text = label;
        var v = ProcUi.Text(11, "FuseboxOnRow", number: true);
        Grid.SetColumn(v, 1);
        head.Children.Add(l);
        head.Children.Add(v);
        g.Children.Add(head);
        var spark = new Sparkline { Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(spark, 1);
        g.Children.Add(spark);
        var col = ProcUi.Text(11, "FuseboxOnRowSecondary");
        col.Text = Strings.L("Collecting…");
        col.HorizontalAlignment = HorizontalAlignment.Center;
        col.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(col, 1);
        g.Children.Add(col);
        return (Card(g, index), v, spark, col);
    }

    /// <summary>Adaptive grid (minimum column 110, spacing 8): n columns for the available width.</summary>
    void PlaceStatGrids()
    {
        PlaceGrid(statGrid, Body.ActualWidth - 28);
        if (historyHost == null) return;
        foreach (var child in historyHost.Children)
            if (child is Grid { Tag: double } g) PlaceGrid(g, Body.ActualWidth - 28);
    }

    static void PlaceGrid(Grid g, double width)
    {
        if (g == null || width <= 0) return;
        int cols = Math.Max(1, (int)((width + 8) / (110 + 8)));
        int n = g.Children.Count;
        cols = Math.Min(cols, Math.Max(n, 1));
        int rows = (n + cols - 1) / cols;
        if (g.ColumnDefinitions.Count != cols)
        {
            g.ColumnDefinitions.Clear();
            for (int i = 0; i < cols; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        if (g.RowDefinitions.Count != rows)
        {
            g.RowDefinitions.Clear();
            for (int i = 0; i < rows; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (int i = 0; i < n; i++) { Grid.SetRow((FrameworkElement)g.Children[i], i / cols); Grid.SetColumn((FrameworkElement)g.Children[i], i % cols); }
    }

    // ---- header and Now ----

    void RenderNow()
    {
        if (!built) return;
        var live = ProcessMonitor.FindRunning(args.Key, name);
        running = live != null;
        if (live != null)
        {
            app = live;
            if (path == null && live.ExePath != null) { path = live.ExePath; _ = LoadAbout(); }   // opened by name: the path is known once it runs
            if (!string.IsNullOrEmpty(live.Name)) name = live.Name;
        }
        var g = app;

        string shown = g != null ? ProcessText.NameOf(g) : name;
        ProcUi.Set(title, shown);
        if (g != null) ProcUi.SetIcon(icon, iconFallback, g);
        else
        {
            var src = ProcessMonitor.IconFor(path);
            icon.Source = src;
            icon.Visibility = src == null ? Visibility.Collapsed : Visibility.Visible;
            iconFallback.Visibility = src == null ? Visibility.Visible : Visibility.Collapsed;
        }

        string? version = path != null ? ProcessSampler.Describe(path, g?.ImageName ?? "").Version : null;
        string state = !running ? Strings.L("Not running")
            : g?.StartedLocal is { } s ? Strings.L("Running for %@", ProcessText.Duration(Math.Max((int)(DateTime.Now - s).TotalMinutes, 1))) : Strings.L("Running");
        ProcUi.Set(status, (version != null ? Strings.L("Version %@ · ", version) : "") + state);

        bool canStop = running && g != null && g.CanStop;
        bring.Visibility = path != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
        string bringText = running ? Strings.L("Bring to front") : Strings.L("Open");
        ((TextBlock)((StackPanel)bring.Content).Children[1]).Text = bringText;
        AutomationProperties.SetName(bring, bringText);
        quit.Visibility = force.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
        reveal.Visibility = path != null ? Visibility.Visible : Visibility.Collapsed;

        nowPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (!running || g == null) return;

        // record one sample per new snapshot (Changed also fires when an icon arrives)
        if (ProcessMonitor.Last is { } snap && snap.Time != lastRecorded) { lastRecorded = snap.Time; cpuHist.Add(g.Cpu); ramHist.Add(g.Ram); }

        ProcUi.Set(cpuNow, ProcessText.Pct(g.Cpu));
        ProcUi.Set(memNow, ProcessText.Mem(g.Ram));
        ProcUi.Set(gpuNow, g.Gpu >= 0.5 ? ProcessText.Pct(Math.Min(g.Gpu, 100)) : "–");
        ProcUi.Set(cpuTitleVal, ProcessText.Pct(g.Cpu));
        ProcUi.Set(ramTitleVal, ProcessText.Mem(g.Ram));
        bool blocks = ThemeService.Current.RowStyle == RowStyle.Blocks;
        cpuSpark.Stroke = ramSpark.Stroke = ThemeService.Brush(blocks ? "FuseboxOnRow" : "FuseboxAccent");
        // the sparklines auto-scale to their own maximum and jump per sample
        cpuCollecting.Visibility = cpuHist.Count < 2 ? Visibility.Visible : Visibility.Collapsed;
        ramCollecting.Visibility = ramHist.Count < 2 ? Visibility.Visible : Visibility.Collapsed;
        if (cpuHist.Count >= 2) cpuSpark.SetValues(cpuHist.Tail(cpuHist.Count).ToList());
        if (ramHist.Count >= 2) ramSpark.SetValues(ramHist.Tail(ramHist.Count).ToList());

        var facts = new List<(string, string)> { (Strings.L("Processes"), g.Pids.Count > 0 ? g.Pids.Count.ToString() : "–"), (Strings.L("Threads"), g.Threads > 0 ? g.Threads.ToString() : "–"),
            (Strings.L("Disk read · write"), $"{Fmt.Rate(g.DiskRead)} · {Fmt.Rate(g.DiskWrite)}") };
        FillFacts(factsHost, facts);
        PlaceStatGrids();
    }

    /// <summary>Label (12, secondary) + value (number 13) rows, updated in place.</summary>
    static void FillFacts(StackPanel host, List<(string Label, string Value)> facts)
    {
        while (host.Children.Count > facts.Count) host.Children.RemoveAt(host.Children.Count - 1);
        while (host.Children.Count < facts.Count)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = ProcUi.Text(12, "FuseboxOnRowSecondary");
            var v = ProcUi.Text(13, "FuseboxOnRow", number: true);
            v.TextTrimming = TextTrimming.CharacterEllipsis;
            v.IsTextSelectionEnabled = false;
            v.TextAlignment = TextAlignment.Right;
            v.MaxWidth = 420;
            Grid.SetColumn(v, 1);
            row.Children.Add(l);
            row.Children.Add(v);
            host.Children.Add(row);
        }
        for (int i = 0; i < facts.Count; i++)
        {
            var row = (Grid)host.Children[i];
            ProcUi.Set((TextBlock)row.Children[0], facts[i].Label);
            var v = (TextBlock)row.Children[1];
            ProcUi.Set(v, facts[i].Value);
            ToolTipService.SetToolTip(v, facts[i].Value);
        }
    }

    void BringOrOpen()
    {
        if (running && app != null) { var pids = app.Pids.ToList(); _ = Task.Run(() => ProcessControl.BringToFront(pids)); }
        else if (path != null)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    // ---- History ----

    void UpdatePeriodChips()
    {
        foreach (var (days, b) in periodChips)
        {
            bool on = days == period;
            b.IsChecked = on;
            b.Foreground = ThemeService.Brush(on ? "FuseboxAccent" : "FuseboxSecondary");
            b.Background = on ? ThemeService.Brush("FuseboxAccentSoft") : null;
            if (b.Content is TextBlock t) t.Foreground = b.Foreground;
        }
    }

    async Task LoadHistory()
    {
        long ticket = ++loadTicket;
        usage = null;
        RenderHistory();
        string n = name;
        int days = period;
        var u = await Task.Run(() => AppUsage.Load(n, days, DateTime.Now));
        if (ticket != loadTicket) return;
        usage = u;
        RenderHistory();
    }

    string When() => period == 1 ? Strings.L("today") : period == 7 ? Strings.L("in the last 7 days") : Strings.L("in the last %lld days", period);

    void RenderHistory()
    {
        if (!built) return;
        cards.RemoveAll(c => historyHost.Children.Contains(c.Card));
        historyHost.Children.Clear();
        if (usage == null)
        {
            var t = ProcUi.Text(12, "FuseboxOnRowSecondary");
            t.Text = Strings.L("Reading the logs…");
            historyHost.Children.Add(Card(t));
            return;
        }
        var u = usage;
        if (u.Minutes == 0)
        {
            var t = ProcUi.Text(12, "FuseboxOnRowSecondary");
            t.Text = Strings.L("Not seen %@ — Fusebox logs open apps once a minute.", When());
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
            historyHost.Children.Add(Card(t));
            return;
        }

        var stats = new Grid { ColumnSpacing = 8, RowSpacing = 8, Tag = 110.0 };
        int i = 0;
        void Add(string label, string value)
        {
            var s = new StackPanel { Spacing = 2 };
            var l = ProcUi.Text(11, "FuseboxOnRowSecondary");
            l.Text = label;
            var v = ProcUi.Text(16, "FuseboxOnRow", number: true);
            v.Text = value;
            s.Children.Add(l);
            s.Children.Add(v);
            stats.Children.Add(Card(s, i++));
        }
        Add(Strings.L("Average CPU"), ProcessText.Pct(u.AvgCpu));
        Add(Strings.L("Peak CPU"), ProcessText.Pct(u.PeakCpu));
        Add(Strings.L("Average memory"), ProcessText.Mem(u.AvgMemGb * 1073741824.0));
        Add(Strings.L("Time open"), ProcessText.Duration(u.Minutes));
        if (u.HasGpu) Add(Strings.L("Average GPU"), ProcessText.Pct(u.AvgGpu));
        historyHost.Children.Add(stats);
        PlaceGrid(stats, Body.ActualWidth - 28);

        // "When you use it": 24 hour bars, and 7 weekday bars (Monday first) for longer periods
        var usageStack = new StackPanel { Spacing = 6 };
        var hl = ProcUi.Text(11, "FuseboxOnRowSecondary");
        hl.Text = Strings.L("When you use it · by hour");
        usageStack.Children.Add(hl);
        usageStack.Children.Add(Bars(u.ByHour, h => h % 6 == 0 ? h.ToString() : "", h => $"{h}:00 – {h + 1}:00 · {ProcessText.Duration((int)u.ByHour[h])}"));
        if (period > 1)
        {
            var dl = ProcUi.Text(11, "FuseboxOnRowSecondary");
            dl.Text = Strings.L("By day of the week");
            dl.Margin = new Thickness(0, 4, 0, 0);
            usageStack.Children.Add(dl);
            var fmt = Strings.Culture.DateTimeFormat;
            usageStack.Children.Add(Bars(u.ByWeekday, d => fmt.GetShortestDayName((DayOfWeek)((d + 1) % 7)),
                d => $"{fmt.GetDayName((DayOfWeek)((d + 1) % 7))} · {ProcessText.Duration((int)u.ByWeekday[d])}"));
        }
        historyHost.Children.Add(Card(usageStack));
        PlaceStatGrids();
    }

    /// <summary>Bars: radius 2, height max(2, 44·v/max), accent (white in Color blocks), 15 % when 0; label 9 under each bar.</summary>
    static Grid Bars(double[] values, Func<int, string> label, Func<int, string> tip)
    {
        double max = Math.Max(values.Max(), 1);
        bool blocks = ThemeService.Current.RowStyle == RowStyle.Blocks;
        var g = new Grid { ColumnSpacing = 2, Height = 60 };
        for (int i = 0; i < values.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var col = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bar = new Border
            {
                Height = Math.Max(2, 44 * values[i] / max), CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom,
                Background = ThemeService.Brush(blocks ? "FuseboxOnRow" : "FuseboxAccent"), Opacity = values[i] > 0 ? 1 : 0.15,
            };
            col.Children.Add(bar);
            var t = ProcUi.Text(9, "FuseboxOnRowSecondary");
            t.Text = label(i);
            t.TextAlignment = TextAlignment.Center;
            t.TextTrimming = TextTrimming.None;
            t.Margin = new Thickness(0, 3, 0, 0);
            t.IsHitTestVisible = false;
            Grid.SetRow(t, 1);
            col.Children.Add(t);
            ToolTipService.SetToolTip(col, tip(i));
            Grid.SetColumn(col, i);
            g.Children.Add(col);
        }
        return g;
    }

    // ---- About ----

    async Task LoadAbout()
    {
        aboutHost.Children.Clear();
        if (path == null) return;
        string p = path, n = name;
        aboutHost.Children.Add(Section(Strings.L("About")));
        if (!FactsCache.TryGetValue(p, out var facts))
        {
            var loading = ProcUi.Text(12, "FuseboxOnRowSecondary");
            loading.Text = Strings.L("Looking it up…");
            aboutHost.Children.Add(Card(loading));
            facts = await Task.Run(() => { try { return AppInfo.Read(p, n); } catch { return null; } });
            FactsCache[p] = facts;
            if (p != path) return;   // another app opened meanwhile
        }
        aboutHost.Children.Clear();
        if (facts == null) return;
        aboutHost.Children.Add(Section(Strings.L("About")));
        var rows = new List<(string, string)>();
        if (facts.Version != null) rows.Add((Strings.L("Version"), facts.Version));
        if (facts.Machine != null)
            rows.Add((Strings.L("Architecture"), facts.Emulated ? Strings.L("%@ (runs via emulation)", facts.Machine) : facts.Machine == "x86" ? Strings.L("%@ (32-bit)", facts.Machine) : facts.Machine));
        if (facts.SizeBytes is { } size) rows.Add((Strings.L("Size on disk"), facts.SizeIsMinimum ? Strings.L("At least %@", Fmt.Bytes(size)) : Fmt.Bytes(size)));
        if (facts.Location != null) rows.Add((Strings.L("Location"), facts.Location));
        if (facts.SignedBy != null) rows.Add((Strings.L("Signed by"), facts.SignedBy));
        if (facts.Installed is { } inst) rows.Add((Strings.L("Installed"), inst.ToString("d MMM yyyy", Strings.Culture)));
        rows.Add((Strings.L("Opens at login"), facts.OpensAtLogin ? Strings.L("Yes") : Strings.L("No")));
        var host = new StackPanel { Spacing = 7 };
        FillFacts(host, rows);
        aboutHost.Children.Add(Card(host, 4));
    }
}
