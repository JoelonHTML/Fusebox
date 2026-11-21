using Fusebox.App.Controls;
using Fusebox.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>
/// The small panel next to the tray icon (the Mac's menu bar panel): compact rows with a mini graph, plus Open / Quit.
/// Borderless, acrylic, hides when it loses focus. Open = LiveHub counts it as watched.
/// </summary>
public sealed partial class TrayPanel : Window
{
    const int WidthDip = 340, RowDip = 44;
    readonly nint hwnd;
    readonly Dictionary<Metric, (TextBlock Value, Sparkline Graph)> rows = [];
    List<Metric> shown = [];
    long hiddenAt;

    public bool IsOpen { get; private set; }

    public TrayPanel()
    {
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        OpenButton.Content = L("Open Fusebox");
        QuitButton.Content = L("Quit Fusebox");
        var p = (OverlappedPresenter)AppWindow.Presenter;
        p.SetBorderAndTitleBar(false, false);
        p.IsResizable = false;
        p.IsMaximizable = false;
        p.IsMinimizable = false;
        AppWindow.IsShownInSwitchers = false;
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) Close_(); };
    }

    public void Toggle()
    {
        if (IsOpen) { Close_(); return; }
        // Clicking the icon while the panel is open first deactivates (hides) it, then arrives here: do not reopen.
        if (Environment.TickCount64 - hiddenAt < 400) return;
        Open();
    }

    void Open()
    {
        BuildRows();
        double scale = Tray.GetDpiForWindow(hwnd) / 96.0;
        int w = (int)(WidthDip * scale), h = (int)((shown.Count * RowDip + 6 * Math.Max(shown.Count - 1, 0) + 28 + 14 + 32) * scale);
        Tray.GetCursorPos(out var pt);
        var work = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest).WorkArea;
        int margin = (int)(8 * scale);
        int x = Math.Clamp(pt.X - w / 2, work.X + margin, work.X + work.Width - w - margin);
        int y = pt.Y < work.Y + work.Height / 2 ? work.Y + margin : work.Y + work.Height - h - margin;   // taskbar at the top or the bottom
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
        IsOpen = true;
        App.Live.Updated += Render;
        AppWindow.Show();
        Activate();
        Tray.SetForegroundWindow(hwnd);
        Render();
    }

    void Close_()
    {
        if (!IsOpen) return;
        IsOpen = false;
        hiddenAt = Environment.TickCount64;
        App.Live.Updated -= Render;
        AppWindow.Hide();
    }

    void BuildRows()
    {
        var s = App.Live.Last;
        var want = Metrics.Parse(Prefs.Str("metrics")).Where(m => s == null || !double.IsNaN(Metrics.Value(m, s))).ToList();
        if (want.SequenceEqual(shown)) return;
        shown = want;
        Rows.Children.Clear();
        foreach (var m in shown)
        {
            if (!rows.TryGetValue(m, out var r))
            {
                var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var graph = new Sparkline { Height = 28, Width = 96, VerticalAlignment = VerticalAlignment.Center };
                rows[m] = r = (value, graph);
            }
            var grid = new Grid { Height = RowDip, ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = Metrics.Label(m), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(r.Value, 1); Grid.SetColumn(r.Graph, 2);
            // A row element can only have one parent: detach from the previous grid first.
            if (r.Value.Parent is Panel old) old.Children.Remove(r.Value);
            if (r.Graph.Parent is Panel old2) old2.Children.Remove(r.Graph);
            grid.Children.Add(label); grid.Children.Add(r.Value); grid.Children.Add(r.Graph);
            Rows.Children.Add(grid);
        }
    }

    void Render()
    {
        if (!IsOpen || App.Live.Last is not { } s) return;
        BuildRows();
        foreach (var m in shown)
        {
            var c = MetricText.Summary(m, s, App.Live);
            rows[m].Value.Text = c.Facts.Length > 0 && m is Metric.Net or Metric.Battery ? $"{c.Value}  {c.Facts[0]}" : $"{c.Value} {c.Unit}".Trim();
            rows[m].Graph.SetValues(App.Live.Values(m, TimeRange.Short), Metrics.Scale(m));
        }
    }

    void Open_Click(object sender, RoutedEventArgs e) { Close_(); App.ShowMain(); }
    void Quit_Click(object sender, RoutedEventArgs e) => App.Quit();
}
