using Fusebox.App.Controls;
using Fusebox.App.Settings;
using Fusebox.Core;
using Fusebox.Core.Look;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>
/// The flyout above the tray icon (FEATURES.md §8.6, Windows: like Quick Settings): anchored to the icon (Shell_NotifyIconGetRect), 336 DIPs
/// wide, themed (theme bg; Mica for Liquid Glass), tool window (no taskbar button, not in Alt+Tab), hides when it loses focus.
/// Exists while the app runs but only works while open: <see cref="Render"/> runs on open and on LiveHub.Updated while open.
///
/// Adding a row (Live agent): build it in <see cref="Build"/> with <see cref="AddRow"/> (a ThemedRow, index = block color), keep the
/// controls you update in fields, and fill them in <see cref="Render"/>. The window height follows the content.
/// </summary>
public sealed partial class TrayFlyout : Window
{
    public const double WidthDip = 336;
    readonly nint hwnd;
    long hiddenAt;
    bool built;

    // Rows of the minimal panel (the Live agent replaces these with the rich panel: score, sparklines, tip).
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly Ellipse dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel values = new() { Spacing = 5 };

    public bool IsOpen { get; private set; }

    public TrayFlyout()
    {
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var p = (OverlappedPresenter)AppWindow.Presenter;
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
        if (!built) { Build(); built = true; }
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

    /// <summary>Builds the rows once. Order = on screen.</summary>
    void Build()
    {
        // Row 0: status sentence + Quit.
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Foreground = ThemeService.Brush("FuseboxOnRow");
        Look.Label(status, 13);
        var quit = IconButton("\uE7E8", L("Quit Fusebox"), App.Quit);
        Grid.SetColumn(status, 1); Grid.SetColumn(quit, 2);
        head.Children.Add(dot); head.Children.Add(status); head.Children.Add(quit);
        AddRow(head, 0);

        // Row 1: live values (CPU, GPU, RAM, NET, BAT that are on Live, in Live order).
        AddRow(values, 1);

        // Quick tiles (not themed rows).
        var tiles = new Grid { ColumnSpacing = 4 };
        (string Glyph, string Title, string Tip, Action Run)[] list =
        [
            ("\uE7C4", "Fusebox", L("Open Fusebox"), () => App.ShowMain()),
            ("\uE9D2", "Checkup", "Checkup", () => App.ShowMain("Checkup")),
            ("\uE71D", L("Processes"), L("Processes"), () => App.ShowMain("Processes")),
            ("\uE9D9", L("Network"), L("Find the problem"), () => { Prefs.Set("netFindNow", true); App.ShowMain("Network"); }),
            ("\uE713", L("Settings"), "", SettingsWindow.Open),
        ];
        for (int i = 0; i < list.Length; i++)
        {
            var (glyph, title, tip, run) = list[i];
            tiles.ColumnDefinitions.Add(new ColumnDefinition());
            var tile = Tile(glyph, title, tip, () => { Hide(); run(); });
            Grid.SetColumn(tile, i);
            tiles.Children.Add(tile);
        }
        Panel.Children.Add(tiles);
    }

    /// <summary>Fills the rows from App.Live (open only; never while hidden).</summary>
    void Render()
    {
        if (!IsOpen) return;
        var (text, level) = App.Live.Status;
        status.Text = text;
        dot.Fill = ThemeService.Brush(level >= 2 ? "FuseboxDanger" : level == 1 ? "FuseboxWarn" : "FuseboxGood");
        var s = App.Live.Last;
        var want = Metrics.Parse(Prefs.Str("metrics")).Where(m => m is Metric.Cpu or Metric.Gpu or Metric.Ram or Metric.Net or Metric.Battery
            && (s == null || !double.IsNaN(Metrics.Value(m, s)))).ToList();
        while (values.Children.Count > want.Count) values.Children.RemoveAt(values.Children.Count - 1);
        for (int i = 0; i < want.Count; i++)
        {
            if (i >= values.Children.Count) values.Children.Add(ValueLine());
            var g = (Grid)values.Children[i];
            ((TextBlock)g.Children[0]).Text = Metrics.Label(want[i]);
            ((TextBlock)g.Children[1]).Text = s == null ? "" : ValueText(want[i], s);
        }
    }

    static string ValueText(Metric m, LiveSample s) => m switch
    {
        Metric.Net => "↓" + Fmt.Short(s.NetIn, Prefs.Bool("netBits")),
        Metric.Ram when Prefs.List("absolute").Contains("ram") => Fmt.One(s.RamUsed / 1073741824.0) + " GB",
        _ => Fmt.Pct(Metrics.Value(m, s)),
    };

    static Grid ValueLine()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var label = new TextBlock { Foreground = ThemeService.Brush("FuseboxOnRowSecondary") };
        Look.Label(label, 11);
        var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Foreground = ThemeService.Brush("FuseboxOnRow") };
        Look.Number(value, 12);
        Grid.SetColumn(value, 1);
        g.Children.Add(label); g.Children.Add(value);
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
        Panel.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        int w = (int)Math.Ceiling(WidthDip * s), h = (int)Math.Ceiling(Panel.DesiredSize.Height * s);
        Box icon;
        if (App.Tray?.IconRect() is { } r) icon = r;
        else { Win32.GetCursorPos(out var pt); icon = new Box(pt.X, pt.Y, 1, 1); }
        var work = DisplayArea.GetFromPoint(new((int)icon.X, (int)icon.Y), DisplayAreaFallback.Nearest).WorkArea;
        var (x, y) = Placement.Flyout(icon, new Box(work.X, work.Y, work.Width, work.Height), w, h, 12 * s);
        AppWindow.MoveAndResize(new((int)x, (int)y, w, h));
    }
}
