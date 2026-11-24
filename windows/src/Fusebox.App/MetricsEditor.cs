using Fusebox.App.Settings;
using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>
/// Which measurements show, and in what order (Settings › Live page, Settings › Tray icon, Setup). Pref <c>key</c> = enabled ids in order
/// (cpu,gpu,ram,net,ssd,io,battery), <c>key</c>+"Hidden" = the order of the switched-off ones, so the order survives toggling (§9.6).
/// Drag rows to reorder (Ctrl+↑/↓ from the keyboard), "Restore defaults" resets both. <c>limit</c> &gt; 0 = at most that many on.
/// </summary>
public static class MetricsEditor
{
    public const string Default = Metrics.DefaultOrder;

    sealed record Entry(Metric Metric, CheckBox Box);

    /// <summary>Setup's one-line explanation per measurement (Windows wording for CPU: no temperature).</summary>
    static string Subtitle(Metric m) => m switch
    {
        Metric.Cpu => L("How hard your PC is working"), Metric.Gpu => L("How hard the graphics chip is working"), Metric.Ram => L("How full the working memory is"),
        Metric.Net => L("Download and upload speed"), Metric.Ssd => L("How full your storage is"),
        Metric.Io => L("How much is being read from and written to storage"), _ => L("Charge and time left"),
    };

    public static List<Metric> Enabled(string key = "metrics") => Metrics.Parse(Prefs.Str(key));

    /// <summary>Enabled first (their order), then the remembered switched-off ones, then the rest.</summary>
    static List<Metric> Order(string key)
    {
        var on = Enabled(key);
        var hidden = Metrics.Parse(Prefs.Str(key + "Hidden")).Where(m => !on.Contains(m));
        var o = on.Concat(hidden).ToList();
        return [.. o, .. Metrics.All.Where(m => !o.Contains(m))];
    }

    public static FrameworkElement Create(string key = "metrics", int limit = 0, bool subtitles = false)
    {
        var root = new StackPanel { Spacing = 6 };
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None, CanReorderItems = true, CanDragItems = true, AllowDrop = true,
        };
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);

        void Save()
        {
            var rows = list.Items.Cast<Grid>().Select(g => (Entry)g.Tag).ToList();
            Prefs.Set(key, Metrics.Join(rows.Where(r => r.Box.IsChecked == true).Select(r => r.Metric)));
            Prefs.Set(key + "Hidden", Metrics.Join(rows.Where(r => r.Box.IsChecked != true).Select(r => r.Metric)));
            Limit();
        }

        void Limit()
        {
            if (limit <= 0) return;
            var rows = list.Items.Cast<Grid>().Select(g => (Entry)g.Tag).ToList();
            bool full = rows.Count(r => r.Box.IsChecked == true) >= limit;
            foreach (var r in rows) r.Box.IsEnabled = !full || r.Box.IsChecked == true;
        }

        void Move(Grid row, int by)
        {
            int i = list.Items.IndexOf(row), j = i + by;
            if (i < 0 || j < 0 || j >= list.Items.Count) return;
            list.Items.RemoveAt(i);
            list.Items.Insert(j, row);
            Save();
            ((Entry)row.Tag).Box.Focus(FocusState.Keyboard);
        }

        void Fill()
        {
            list.Items.Clear();
            var on = Enabled(key);
            foreach (var m in Order(key))
            {
                var box = new CheckBox { IsChecked = on.Contains(m), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = Metrics.Title(m) });
                if (subtitles) label.Children.Add(new TextBlock { Text = Subtitle(m), FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
                var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(label, 1);
                var grip = new FontIcon { Glyph = "", FontSize = 12, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(grip, 2);
                row.Children.Add(box); row.Children.Add(label); row.Children.Add(grip);
                row.Tag = new Entry(m, box);
                var r = row;
                box.Click += (_, _) => Save();
                box.KeyDown += (_, e) =>
                {
                    if (e.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
                    if (!Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
                    e.Handled = true;
                    Move(r, e.Key == VirtualKey.Up ? -1 : 1);
                };
                list.Items.Add(row);
            }
            Limit();
        }

        list.DragItemsCompleted += (_, _) => Save();
        var restore = new Button { Content = L("Restore defaults"), HorizontalAlignment = HorizontalAlignment.Left };
        restore.Click += (_, _) => { Prefs.Reset(key); Prefs.Reset(key + "Hidden"); Fill(); };
        Fill();
        root.Children.Add(list);
        root.Children.Add(restore);
        // Changed from elsewhere (a profile, import, reset): redraw. Our own saves already match what is shown.
        Kit.WhilePrefs(root, k =>
        {
            if (k != key) return;
            var shown = Metrics.Join(list.Items.Cast<Grid>().Select(g => (Entry)g.Tag).Where(r => r.Box.IsChecked == true).Select(r => r.Metric));
            if (shown != Metrics.Join(Enabled(key))) Fill();
        });
        return root;
    }
}
