using Fusebox.App.Controls;
using Fusebox.App.Settings;
using Fusebox.Core;
using Fusebox.Core.Live;
using Fusebox.Core.Look;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>Score and tip of Checkup's gist (Mac Analysis.gist) for the rich panel's first and third row.</summary>
public sealed record TrayGist(int? Score, string Reason, string? TipHeadline = null, string? TipDetail = null, bool TipSerious = false);

/// <summary>
/// The flyout above the tray icon (FEATURES.md §8.6/§8.7, Windows: like Quick Settings): anchored to the icon (Shell_NotifyIconGetRect),
/// themed (theme bg; Mica for Liquid Glass), tool window (no taskbar button, not in Alt+Tab), hides when it loses focus. Exists while the
/// app runs but only works while open: <see cref="Render"/> runs on open and on LiveHub.Updated while open.
///
/// Rich panel (pref richPanel, default; 336 wide): row 0 score (or the status sentence) + Quit, row 1 live lines CPU/GPU/RAM/NET/BAT with a
/// 14 px sparkline of the last 30 samples, row 2 one tip, then five quick tiles. Classic panel (richPanel off; 260 wide): status, values,
/// heaviest 3 apps, links (follows menuParts). <see cref="GistProvider"/> = the Checkup agent's hook for the score and the tip.
/// </summary>
public sealed partial class TrayFlyout : Window
{
    public const double WidthDip = 336, ClassicWidthDip = 260;
    static bool Rich => Prefs.Bool("richPanel");
    static double Wd => Rich ? WidthDip : ClassicWidthDip;

    /// <summary>Checkup agent: return today's gist (score 1–10 or null while loading, the reason sentence, the first serious tip) or null when none yet.</summary>
    public static Func<TrayGist?>? GistProvider { get; set; }

    readonly nint hwnd;
    long hiddenAt;
    bool built, builtRich;

    // rich panel
    readonly Ellipse dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel scoreBox = new() { Orientation = Orientation.Horizontal, Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly TextBlock score = new(), scoreOf = new() { Text = "/10", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
    readonly TextBlock headline = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock reason = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StackPanel lines = new() { Spacing = 5 };
    readonly List<(Metric M, TextBlock Value, Sparkline Spark)> lineCells = [];
    ThemedRow? tipRow;
    readonly FontIcon tipIcon = new() { FontSize = 12, Margin = new Thickness(0, 1, 0, 0) };
    readonly TextBlock tipHead = new() { TextWrapping = TextWrapping.Wrap }, tipBody = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };

    // classic panel
    readonly Ellipse cDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock cStatus = new() { TextWrapping = TextWrapping.Wrap };
    readonly StackPanel cValues = new() { Spacing = 4 };
    readonly StackPanel cTop = new() { Spacing = 4 };

    public bool IsOpen { get; private set; }

    public TrayFlyout()
    {
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Own presenter instead of casting AppWindow.Presenter (that cast threw InvalidCastException under Native AOT in CI).
        var p = OverlappedPresenter.Create();
        AppWindow.SetPresenter(p);
        p.SetBorderAndTitleBar(true, false);   // keeps the Windows 11 rounded corners and shadow
        p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
        p.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, (nint)((ex | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW));
        ThemeService.Attach(this);
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) Hide(); };
        Panel.SizeChanged += (_, _) => { if (IsOpen) Place(); };
    }

    /// <summary>Tray left click: open, or close when open. A click that just closed it (deactivate) does not reopen it.</summary>
    public void Toggle()
    {
        if (IsOpen) { Hide(); return; }
        if (Environment.TickCount64 - hiddenAt < 400) return;
        Open();
    }

    public void Open()
    {
        if (!built || builtRich != Rich) { Build(); built = true; builtRich = Rich; }
        IsOpen = true;
        Render();
        Place();
        App.Live.Updated += Render;
        AppWindow.Show();
        Activate();
        SettingsNative.SetForegroundWindow(hwnd);
    }

    public void Hide()
    {
        if (!IsOpen) return;
        IsOpen = false;
        hiddenAt = Environment.TickCount64;
        App.Live.Updated -= Render;
        AppWindow.Hide();
    }

    /// <summary>Adds a themed row (Mac themedRow index: 0 score, 1 values, 2 tip …). Returns it, to show/hide later.</summary>
    public ThemedRow AddRow(UIElement content, int index)
    {
        var row = new ThemedRow { Index = index, Child = content };
        Panel.Children.Add(row);
        return row;
    }

    static Brush B(string key) => ThemeService.Brush(key);

    // ---- building ----

    void Build()
    {
        Panel.Children.Clear();
        lineCells.Clear(); lines.Children.Clear();
        Panel.Width = Wd;
        Panel.Spacing = Rich ? 10 : 8;
        if (Rich) BuildRich(); else BuildClassic();
    }

    void BuildRich()
    {
        // Row 0: score (or status dot) · "Health" + reason (or the status sentence) · Quit
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Look.Number(score, 32); Look.Number(scoreOf, 12);
        scoreOf.Foreground = B("FuseboxOnRowSecondary");
        scoreBox.Children.Clear(); scoreBox.Children.Add(score); scoreBox.Children.Add(scoreOf);
        var lead = new Grid();
        lead.Children.Add(scoreBox); lead.Children.Add(dot);
        headline.Foreground = B("FuseboxOnRow"); Look.Label(headline, 13); headline.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        reason.Foreground = B("FuseboxOnRowSecondary"); Look.Label(reason, 11);
        var col = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(headline); col.Children.Add(reason);
        var quit = IconButton("", L("Quit Fusebox"), App.Quit);
        Grid.SetColumn(col, 1); Grid.SetColumn(quit, 2);
        head.Children.Add(lead); head.Children.Add(col); head.Children.Add(quit);
        AddRow(head, 0);

        // Row 1: live lines
        AddRow(lines, 1);

        // Row 2: the tip (only with a gist that has one); the whole row opens Checkup
        var tip = new Grid { ColumnSpacing = 8 };
        tip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        tip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tcol = new StackPanel { Spacing = 1 };
        tipHead.Foreground = B("FuseboxOnRow"); Look.Label(tipHead, 12); tipHead.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        tipBody.Foreground = B("FuseboxOnRowSecondary"); Look.Label(tipBody, 11);
        tcol.Children.Add(tipHead); tcol.Children.Add(tipBody);
        Grid.SetColumn(tcol, 1);
        tip.Children.Add(tipIcon); tip.Children.Add(tcol);
        tipRow = AddRow(tip, 2);
        tipRow.Visibility = Visibility.Collapsed;
        tipRow.Tapped += (_, _) => { Hide(); App.ShowMain("Checkup"); };

        Panel.Children.Add(Tiles());
    }

    Grid Tiles()
    {
        var tiles = new Grid { ColumnSpacing = 4 };
        (string Glyph, string Title, string Tip, Action Run)[] list =
        [
            ("", "Fusebox", L("Open Fusebox"), () => App.ShowMain()),
            ("", "Checkup", "Checkup", () => App.ShowMain("Checkup")),
            ("", L("Processes"), L("Processes"), () => App.ShowMain("Processes")),
            ("", L("Network"), L("Find the problem"), () => { Prefs.Set("netFindNow", true); App.ShowMain("Network"); }),
            ("", L("Settings"), "", SettingsWindow.Open),
        ];
        for (int i = 0; i < list.Length; i++)
        {
            var (glyph, title, tip, run) = list[i];
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            var tile = Tile(glyph, title, tip, () => { Hide(); run(); });
            Grid.SetColumn(tile, i);
            tiles.Children.Add(tile);
        }
        return tiles;
    }

    void BuildClassic()
    {
        Panel.Spacing = 8;
        var parts = Prefs.List("menuParts");
        if (parts.Contains("status"))
        {
            var h = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            cStatus.Foreground = B("FuseboxText"); Look.Label(cStatus, 14);
            h.Children.Add(cDot); h.Children.Add(cStatus);
            Panel.Children.Add(h);
        }
        if (parts.Contains("values")) Panel.Children.Add(cValues);
        if (parts.Contains("top"))
        {
            Panel.Children.Add(Divider());
            Panel.Children.Add(cTop);
        }
        Panel.Children.Add(Divider());
        var links = new StackPanel();
        if (parts.Contains("links"))
        {
            links.Children.Add(LinkButton(L("Why is my PC slow?"), () => App.ShowMain("Checkup")));
            links.Children.Add(LinkButton(L("Open Fusebox"), () => App.ShowMain()));
            links.Children.Add(LinkButton(L("Setup…"), Boot.ShowSetup));
        }
        links.Children.Add(LinkButton(L("Settings…"), SettingsWindow.Open));
        links.Children.Add(LinkButton(L("Quit Fusebox"), App.Quit));
        Panel.Children.Add(links);
    }

    static Rectangle Divider() => new() { Height = 1, Fill = B("FuseboxLine"), HorizontalAlignment = HorizontalAlignment.Stretch };

    Button LinkButton(string text, Action run)
    {
        var t = new TextBlock { Text = text, Foreground = B("FuseboxText"), HorizontalAlignment = HorizontalAlignment.Left };
        Look.Label(t, 13);
        var b = new Button { Content = t, Background = null, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 4, 6, 4) };
        b.Click += (_, _) => { Hide(); run(); };
        return b;
    }

    // ---- rendering (open only; never while hidden) ----

    static Brush LevelBrush(int level) =>
        ThemeService.Current.RowStyle == RowStyle.Blocks || level == 0 ? B("FuseboxOnRow") : B(level >= 2 ? "FuseboxDanger" : "FuseboxWarn");

    /// <summary>Fills the rows from App.Live and the gist.</summary>
    void Render()
    {
        if (!IsOpen) return;
        if (Rich) RenderRich(); else RenderClassic();
    }

    static string TextOf(Metric m, LiveSample s, LiveOptions o) =>
        m == Metric.Net ? TrayInfo.MenuValue(m, s, o) : LiveCards.PointText(m, s, o);

    void RenderRich()
    {
        var (status, level) = App.Live.Status;
        var gist = GistProvider?.Invoke();
        bool blocks = ThemeService.Current.RowStyle == RowStyle.Blocks;
        if (gist != null)
        {
            scoreBox.Visibility = Visibility.Visible; dot.Visibility = Visibility.Collapsed;
            score.Text = gist.Score?.ToString() ?? "–";
            score.Foreground = blocks ? B("FuseboxOnRow") : gist.Score is not { } sc ? B("FuseboxSecondary") : sc >= 8 ? B("FuseboxGood") : sc >= 5 ? B("FuseboxWarn") : B("FuseboxDanger");
            headline.Text = L("Health");
            reason.Text = gist.Reason.Length > 0 ? gist.Reason : L("Checking…");
            reason.Visibility = Visibility.Visible;
        }
        else
        {
            // No Checkup gist: the status sentence with its colour dot.
            scoreBox.Visibility = Visibility.Collapsed; dot.Visibility = Visibility.Visible;
            dot.Fill = blocks ? B("FuseboxOnRow") : B(level >= 2 ? "FuseboxDanger" : level == 1 ? "FuseboxWarn" : "FuseboxGood");
            headline.Text = status;
            reason.Visibility = Visibility.Collapsed;
        }
        if (tipRow != null)
        {
            bool tip = gist?.TipHeadline is { Length: > 0 };
            tipRow.Visibility = tip ? Visibility.Visible : Visibility.Collapsed;
            if (tip)
            {
                tipIcon.Glyph = gist!.TipSerious ? "" : "";
                tipIcon.Foreground = B(gist.TipSerious ? "FuseboxWarn" : "FuseboxAccent");
                tipHead.Text = gist.TipHeadline!;
                tipBody.Text = gist.TipDetail ?? "";
                tipBody.Visibility = tipBody.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // live lines: only CPU, GPU, RAM, NET, BAT that are on Live, in Live order
        var s = App.Live.Last;
        var want = MetricText.Enabled(s).Where(m => m is Metric.Cpu or Metric.Gpu or Metric.Ram or Metric.Net or Metric.Battery).ToList();
        if (!want.SequenceEqual(lineCells.Select(c => c.M)))
        {
            lines.Children.Clear(); lineCells.Clear();
            foreach (var m in want) lineCells.Add(AddLine(m));
        }
        if (s == null) return;
        var o = MetricText.Options();
        var recent = App.Live.History.Recent;
        var last30 = recent.Skip(Math.Max(recent.Count - 30, 0)).ToList();
        foreach (var (m, value, spark) in lineCells)
        {
            var c = LiveCards.Card(m, s, o);
            value.Text = TextOf(m, s, o);
            value.Foreground = LevelBrush(c.Level);
            spark.Update(Metrics.Values(last30, m), null, Metrics.Scale(m));
        }
    }

    (Metric, TextBlock, Sparkline) AddLine(Metric m)
    {
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        string glyph = m switch { Metric.Cpu => "", Metric.Gpu => "", Metric.Ram => "", Metric.Net => "", _ => "" };
        var icon = new FontIcon { Glyph = glyph, FontSize = 10 * ThemeService.Current.Scale, Foreground = B("FuseboxGraph"), HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock { Text = Metrics.Label(m), Foreground = B("FuseboxOnRowSecondary"), VerticalAlignment = VerticalAlignment.Center };
        Look.Label(label, 11);
        var spark = new Sparkline { Height = 14, VerticalAlignment = VerticalAlignment.Center };
        // Fill the 78 px column and right-align the text inside it: a right-aligned auto-width TextBlock got clipped at its right edge in CI.
        var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Stretch, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, Foreground = B("FuseboxOnRow") };
        Look.Number(value, 12);
        Grid.SetColumn(label, 1); Grid.SetColumn(spark, 2); Grid.SetColumn(value, 3);
        g.Children.Add(icon); g.Children.Add(label); g.Children.Add(spark); g.Children.Add(value);
        lines.Children.Add(g);
        return (m, value, spark);
    }

    void RenderClassic()
    {
        var (status, level) = App.Live.Status;
        cDot.Fill = B(level >= 2 ? "FuseboxDanger" : level == 1 ? "FuseboxWarn" : "FuseboxGood");
        cStatus.Text = status;
        var s = App.Live.Last;
        if (s != null && Prefs.List("menuParts").Contains("values"))
        {
            var o = MetricText.Options();
            var want = MetricText.Enabled(s);
            while (cValues.Children.Count > want.Count) cValues.Children.RemoveAt(cValues.Children.Count - 1);
            for (int i = 0; i < want.Count; i++)
            {
                if (i >= cValues.Children.Count) cValues.Children.Add(ClassicLine());
                var g = (Grid)cValues.Children[i];
                var c = LiveCards.Card(want[i], s, o);
                ((TextBlock)g.Children[0]).Text = Metrics.Label(want[i]);
                var v = (TextBlock)g.Children[1];
                v.Text = c.Value.Length == 0 ? "–" : c.Value;
                v.Foreground = LevelBrush(c.Level);
                ((TextBlock)g.Children[2]).Text = string.Join(" ", new[] { c.Unit, c.Side }.Where(x => x.Length > 0));
            }
        }
        if (App.Live.Apps is { } apps && Prefs.List("menuParts").Contains("top"))
        {
            var top = apps.Where(g => g.Kind != Fusebox.Core.Processes.AppKind.Windows).OrderByDescending(g => g.Cpu).Take(3).ToList();
            cTop.Visibility = top.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            cTop.Children.Clear();
            foreach (var g in top)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var n = new TextBlock { Text = g.Name, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Foreground = B("FuseboxText") };
                var v = new TextBlock { Text = Fmt.Pct(g.Cpu), Foreground = B("FuseboxText") };
                Look.Label(n, 12); Look.Number(v, 12);
                Grid.SetColumn(v, 1);
                row.Children.Add(n); row.Children.Add(v);
                cTop.Children.Add(row);
            }
        }
        else cTop.Visibility = Visibility.Collapsed;
    }

    static Grid ClassicLine()
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Foreground = B("FuseboxSecondary") };
        var value = new TextBlock { Foreground = B("FuseboxText") };
        var rest = new TextBlock { Foreground = B("FuseboxSecondary") };
        Look.Label(label, 12); Look.Number(value, 13); Look.Number(rest, 12);
        Grid.SetColumn(value, 1); Grid.SetColumn(rest, 2);
        g.Children.Add(label); g.Children.Add(value); g.Children.Add(rest);
        return g;
    }

    static Button IconButton(string glyph, string tip, Action run)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Background = null, BorderThickness = new Thickness(0),
            Padding = new Thickness(6), Foreground = ThemeService.Brush("FuseboxOnRowSecondary"),
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => run();
        return b;
    }

    static Button Tile(string glyph, string title, string tip, Action run)
    {
        var content = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 * ThemeService.Current.Scale, Foreground = ThemeService.Brush("FuseboxAccent") });
        var t = new TextBlock { Text = title, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeService.Brush("FuseboxOnRow") };
        Look.Label(t, 10);
        content.Children.Add(t);
        var b = new Button
        {
            Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(2, 7, 2, 7),
            CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0), Background = ThemeService.Brush("FuseboxHover"),
        };
        if (tip.Length > 0) ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, title);
        b.Click += (_, _) => run();
        return b;
    }

    /// <summary>Above the taskbar next to the icon; height = content.</summary>
    void Place()
    {
        double s = Win32.Scale(hwnd);
        Panel.Measure(new Windows.Foundation.Size(Wd, double.PositiveInfinity));
        int w = (int)Math.Ceiling(Wd * s), h = (int)Math.Ceiling(Panel.DesiredSize.Height * s);
        Box icon;
        if (App.Tray?.IconRect() is { } r) icon = r;
        else { Win32.GetCursorPos(out var pt); icon = new Box(pt.X, pt.Y, 1, 1); }
        var work = DisplayArea.GetFromPoint(new((int)icon.X, (int)icon.Y), DisplayAreaFallback.Nearest).WorkArea;
        // Size the CLIENT area to the panel: MoveAndResize sets the outer size, which includes Windows 11's invisible
        // resize borders, so the panel was clipped on the right (seen in CI). Place with the resulting outer size.
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(w, h));
        var outer = AppWindow.Size;
        var (x, y) = Placement.Flyout(icon, new Box(work.X, work.Y, work.Width, work.Height), outer.Width, outer.Height, 12 * s);
        AppWindow.Move(new((int)x, (int)y));
    }
}
