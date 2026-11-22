using Fusebox.App.Controls;
using Fusebox.Core;
using Fusebox.Core.Look;
using Fusebox.Core.Processes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fusebox.App.Pages;

/// <summary>Small shared builders for the Processes page and the app page (flat buttons, icon + text pairs, the icon cell).</summary>
static class ProcUi
{
    /// <summary>The flat, hover-less button style; ProcessesPage and AppPage each define it in their XAML resources and register it here before building rows.</summary>
    public static Style Flat { get; set; } = null!;

    public static TextBlock Text(double label, string brush = "FuseboxOnRow", bool number = false)
    {
        var t = new TextBlock { Foreground = ThemeService.Brush(brush), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        if (number) Look.Number(t, label); else Look.Label(t, label);
        return t;
    }

    public static FontIcon Glyph(string glyph, double size, string brush) =>
        new() { Glyph = glyph, FontSize = size, Foreground = ThemeService.Brush(brush), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Flat text button with a leading Segoe Fluent glyph (label 11).</summary>
    public static Button IconButton(string glyph, string text, string brush, string? tip = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(Glyph(glyph, 12, brush));
        var t = Text(11, brush);
        t.Text = text;
        row.Children.Add(t);
        var b = new Button { Style = Flat, Content = row, Padding = new Thickness(0, 4, 0, 4) };
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>The app icon (18 / 28 / 36 px) or, without one, a Segoe Fluent glyph: gear for system parts, command prompt for background programs.</summary>
    public static (Grid Host, Image Img, FontIcon Fallback) IconCell(double size)
    {
        var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        var fb = Glyph("", size * 0.8, "FuseboxOnRowSecondary");
        fb.HorizontalAlignment = HorizontalAlignment.Center;
        var host = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(fb);
        host.Children.Add(img);
        return (host, img, fb);
    }

    public static void SetIcon(Image img, FontIcon fb, AppGroup g)
    {
        var src = ProcessMonitor.IconFor(g.ExePath);
        if (!ReferenceEquals(img.Source, src)) img.Source = src;
        img.Visibility = src == null ? Visibility.Collapsed : Visibility.Visible;
        fb.Visibility = src == null ? Visibility.Visible : Visibility.Collapsed;
        string glyph = g.Kind == AppKind.Windows ? "" : g.Kind == AppKind.Background ? "" : "";
        if (fb.Glyph != glyph) fb.Glyph = glyph;
    }

    public static void Set(TextBlock t, string text) { if (t.Text != text) t.Text = text; }

    /// <summary>Verdict colour: good / warn (white in Color blocks, like the Mac).</summary>
    public static Brush VerdictBrush(ProcessText.Verdict v) =>
        ThemeService.Current.RowStyle == RowStyle.Blocks ? ThemeService.Brush("FuseboxOnRow") : ThemeService.Brush(v == ProcessText.Verdict.Normal ? "FuseboxGood" : "FuseboxWarn");
}

/// <summary>
/// One row of the list (§4.7): themed row, summary line (icon, name + explanation, columns, stop) and, when open, the details.
/// Built once per app key and updated in place, so a tick only touches text that changed.
/// </summary>
sealed partial class ProcRowView
{
    static readonly Col[] Order = [Col.Cpu, Col.Gpu, Col.Ram, Col.Disk, Col.Threads, Col.Procs, Col.Running];

    public readonly Border Root = new();
    readonly Border selBg = new() { CornerRadius = new CornerRadius(6) };
    readonly Grid cols = new();
    readonly Button hit = new() { Style = ProcUi.Flat, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) };
    readonly Button stop = new() { Style = ProcUi.Flat, Width = 18, Height = 18, Opacity = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly FontIcon stopGlyph = ProcUi.Glyph("", 13, "FuseboxOnRowSecondary");
    readonly Image icon;
    readonly FontIcon iconFallback;
    readonly TextBlock name = ProcUi.Text(12), expl = ProcUi.Text(10, "FuseboxOnRowSecondary");
    readonly Dictionary<Col, TextBlock> cells = [];
    StackPanel? cpuCell;   // own reference: FrameworkElement.Parent is null until the row is loaded (crashed on arm64 CI)
    readonly Grid cpuBarHost = new() { Height = 3, HorizontalAlignment = HorizontalAlignment.Right };
    readonly Border cpuTrack = new() { CornerRadius = new CornerRadius(1.5), Opacity = 0.2 };
    readonly Border cpuFill = new() { CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left };
    ProcDetails? details;
    bool pointerOver, focusWithin;
    double cpuColWidth = 58;
    double cpu;
    int index;
    bool styled;

    public string Key { get; }
    public AppGroup? Group { get; private set; }
    public bool Expanded { get; private set; }

    /// <summary>The summary was clicked / activated.</summary>
    public event Action<ProcRowView>? Clicked;
    public event Action<ProcRowView>? StopClicked;

    public ProcRowView(string key)
    {
        Key = key;
        var (host, img, fb) = ProcUi.IconCell(18);
        icon = img; iconFallback = fb;

        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(host, 0);
        cols.Children.Add(host);

        var nameBlock = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        nameBlock.Children.Add(name);
        nameBlock.Children.Add(expl);
        Grid.SetColumn(nameBlock, 1);
        cols.Children.Add(nameBlock);

        for (int i = 0; i < Order.Length; i++)
        {
            cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            FrameworkElement cell;
            var t = ProcUi.Text(i == 3 ? 11 : 12, "FuseboxOnRow", number: true);
            t.HorizontalAlignment = HorizontalAlignment.Right;
            t.TextAlignment = TextAlignment.Right;
            t.TextTrimming = TextTrimming.Clip;
            cells[Order[i]] = t;
            if (Order[i] == Col.Cpu)
            {
                cpuBarHost.Children.Add(cpuTrack);
                cpuBarHost.Children.Add(cpuFill);
                var stack = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(t);
                stack.Children.Add(cpuBarHost);
                cell = cpuCell = stack;
            }
            else { t.VerticalAlignment = VerticalAlignment.Center; cell = t; }
            cell.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(cell, i + 2);
            cols.Children.Add(cell);
        }

        hit.Content = cols;
        hit.Click += (_, _) => Clicked?.Invoke(this);

        stop.Content = stopGlyph;
        stop.Click += (_, _) => StopClicked?.Invoke(this);
        // Never collapsed: the slot keeps its width, so rows without Stop (Windows itself, Fusebox) stay aligned with the others.
        stop.IsHitTestVisible = stop.IsTabStop = false;

        // Summary = [hit button spanning the columns][stop slot]
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(hit);
        Grid.SetColumn(stop, 1);
        line.Children.Add(stop);
        selBg.Child = line;

        Root.Child = new StackPanel { Children = { selBg } };

        // Stop is shown while the pointer is over the row and while keyboard focus is inside it (accessibility); no hover highlight.
        Root.PointerEntered += (_, _) => { pointerOver = true; UpdateStop(); };
        Root.PointerExited += (_, _) => { pointerOver = false; UpdateStop(); };
        Root.GotFocus += (_, _) => { focusWithin = true; UpdateStop(); };
        Root.LostFocus += (_, _) => { focusWithin = false; UpdateStop(); };
    }

    public ProcDetails EnsureDetails(Func<ProcDetails> make)
    {
        if (details != null) return details;
        details = make();
        ((StackPanel)Root.Child).Children.Add(details.Root);
        details.Root.Visibility = Visibility.Collapsed;
        return details;
    }

    public ProcDetails? Details => details;

    void UpdateStop()
    {
        bool show = pointerOver || focusWithin;
        stop.Opacity = show && stop.IsHitTestVisible ? 1 : 0;
        stopGlyph.Foreground = ThemeService.Brush(ThemeService.Current.RowStyle == RowStyle.Blocks ? "FuseboxOnRow" : "FuseboxDanger");
    }

    public void Restyle(int rowIndex, bool force = false)
    {
        if (!force && styled && index == rowIndex) return;
        styled = true;
        index = rowIndex;
        ThemedRow.Apply(Root, rowIndex);
        UpdateBar();
        UpdateStop();
    }

    public void SetSelected(bool selected) =>
        selBg.Background = selected ? ThemeService.Brush("FuseboxAccentSoft") : null;

    public void SetExpanded(bool open)
    {
        Expanded = open;
        if (details != null) details.Root.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(hit, name.Text);
    }

    /// <summary>Show exactly these columns (Cpu is always first), each at its width; the rest are collapsed.</summary>
    public void SetColumns(IReadOnlyList<(Col Col, double Width)> visible)
    {
        for (int i = 0; i < Order.Length; i++)
        {
            var match = visible.FirstOrDefault(v => v.Col == Order[i]);
            bool on = visible.Any(v => v.Col == Order[i]);
            cols.ColumnDefinitions[i + 2].Width = new GridLength(on ? match.Width + 8 : 0);
            cells[Order[i]].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (Order[i] == Col.Cpu) { cpuColWidth = match.Width; cpuBarHost.Width = match.Width; if (cpuCell != null) cpuCell.Visibility = Visibility.Visible; }
        }
        UpdateBar();
    }

    public void Update(AppGroup g, DateTime now)
    {
        Group = g;
        string nm = ProcessText.NameOf(g);
        ProcUi.Set(name, nm);
        string ex = ProcessText.ExplanationOf(g);
        ProcUi.Set(expl, ex);
        expl.Visibility = ex.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProcUi.SetIcon(icon, iconFallback, g);

        cpu = g.Cpu;
        ProcUi.Set(cells[Col.Cpu], ProcessText.Pct(g.Cpu));
        ProcUi.Set(cells[Col.Gpu], g.Gpu >= 0.5 ? ProcessText.Pct(Math.Min(g.Gpu, 100)) : "–");
        ProcUi.Set(cells[Col.Ram], ProcessText.Mem(g.Ram));
        double io = g.DiskRead + g.DiskWrite;
        ProcUi.Set(cells[Col.Disk], io >= 1000 ? Fmt.Rate(io) : "–");
        ProcUi.Set(cells[Col.Threads], g.Threads > 0 ? g.Threads.ToString() : "–");
        ProcUi.Set(cells[Col.Procs], g.Pids.Count > 0 ? g.Pids.Count.ToString() : "–");
        ProcUi.Set(cells[Col.Running], g.StartedLocal is { } s ? ProcessText.Duration(Math.Max((int)(now - s).TotalMinutes, 1)) : "–");
        UpdateBar();

        bool canStop = g.CanStop;
        stop.IsHitTestVisible = stop.IsTabStop = canStop;
        AutomationProperties.SetAccessibilityView(stop, canStop ? Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Content : Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        string stopName = Strings.L("Stop %@", nm);
        AutomationProperties.SetName(stop, stopName);
        ToolTipService.SetToolTip(stop, stopName);
        UpdateStop();
    }

    void UpdateBar()
    {
        bool blocks = ThemeService.Current.RowStyle == RowStyle.Blocks;
        var brush = ThemeService.Brush(blocks ? "FuseboxOnRow" : cpu >= 50 ? "FuseboxWarn" : "FuseboxAccent");
        if (!ReferenceEquals(cpuTrack.Background, brush)) { cpuTrack.Background = brush; cpuFill.Background = brush; }
        double w = Math.Round(cpuColWidth * Math.Clamp(cpu / 100, 0, 1));
        if (cpuFill.Width != w) cpuFill.Width = w;
    }
}

/// <summary>A search result for an installed app that is not running (§4.5): icon, name, "Not running", chevron; opens the app page.</summary>
sealed partial class InstalledRowView
{
    public readonly Border Root = new();
    public InstalledApp App { get; }
    public event Action<InstalledRowView>? Clicked;
    readonly Image icon;
    readonly FontIcon fallback;

    public InstalledRowView(InstalledApp app)
    {
        App = app;
        var (host, img, fb) = ProcUi.IconCell(18);
        icon = img; fallback = fb;
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(host);
        var n = ProcUi.Text(12);
        n.Text = app.Name;
        Grid.SetColumn(n, 1);
        n.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(n);
        var nr = ProcUi.Text(10, "FuseboxOnRowSecondary");
        nr.Text = Strings.L("Not running");
        nr.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(nr, 2);
        g.Children.Add(nr);
        var chev = ProcUi.Glyph("", 10, "FuseboxOnRowSecondary");
        Grid.SetColumn(chev, 3);
        g.Children.Add(chev);
        var b = new Button { Style = ProcUi.Flat, Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) };
        b.Click += (_, _) => Clicked?.Invoke(this);
        string tip = Strings.L("Usage over time and info about %@", app.Name);
        ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetName(b, app.Name + ", " + Strings.L("Not running"));
        Root.Child = b;
        TryIcon();
    }

    public void TryIcon()
    {
        var src = ProcessMonitor.IconFor(App.Path);
        icon.Source = src;
        icon.Visibility = src == null ? Visibility.Collapsed : Visibility.Visible;
        fallback.Visibility = src == null ? Visibility.Visible : Visibility.Collapsed;
        fallback.Glyph = "";
    }

    public void Restyle(int index, bool force = false)
    {
        if (!force && styled && last == index) return;
        styled = true; last = index;
        ThemedRow.Apply(Root, index);
    }
    bool styled;
    int last;
}
