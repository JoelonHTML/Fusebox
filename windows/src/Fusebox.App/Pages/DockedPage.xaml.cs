using Fusebox.Core.Docked;
using Fusebox.Platform.Docked;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Devices.Enumeration;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>
/// Docked (spec §7). Reads on appear, 0.6 s after a device change (DeviceWatchers started off the UI thread) and every 5 s from the
/// Live clock while the window is watched. The view is rebuilt from the snapshot only when its signature changed (or a pref, the theme,
/// the width band or the selection changed). Nothing runs while the page is not shown.
/// </summary>
public sealed partial class DockedPage : Page
{
    // USB device interfaces, Bluetooth classic endpoints, audio render endpoints, monitors.
    const string UsbFilter = "System.Devices.InterfaceClassGuid:=\"{A5DCBF10-6530-11D2-901F-00C04FB951ED}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";
    const string BtFilter = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";
    static readonly string[] PrefKeys = ["dockedInternals", "dockedEmpty", "dockedDisplays", "dockedWireless", "dockedBytes"];

    readonly List<DeviceWatcher> watchers = new();
    readonly Dictionary<string, Button> buttons = new();
    DispatcherQueueTimer? debounce;
    int generation, watchSession;
    bool reading, again, wide, pairedOpen, otherOpen;
    long lastRead;
    DockedSnapshot? snap;
    DockedView? view;
    string lastSig = "";
    string? selected, refocus;

    public DockedPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        debounce = DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromSeconds(0.6);
        debounce.IsRepeating = false;
        debounce.Tick += (_, _) => Reload();
        App.Live.Updated += OnTick;
        Prefs.Changed += OnPref;
        ThemeService.ThemeChanged += OnTheme;
        Scroller.SizeChanged += OnSize;
        if (snap != null) Draw(true);   // last picture straight away, the new read follows
        Reload();
        StartWatchers();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        generation++;   // drops a read that is still running
        debounce?.Stop();
        debounce = null;
        App.Live.Updated -= OnTick;
        Prefs.Changed -= OnPref;
        ThemeService.ThemeChanged -= OnTheme;
        Scroller.SizeChanged -= OnSize;
        watchSession++;
        StopAll(watchers);
        watchers.Clear();
        reading = again = false;
    }

    // ----- refresh -----

    /// <summary>The Live clock only ticks while the window is watched, so this is the "5 s timer only while visible".</summary>
    void OnTick()
    {
        if (Environment.TickCount64 - lastRead >= 5000) Reload();
    }

    void OnPref(string key)
    {
        if (Array.IndexOf(PrefKeys, key) >= 0) DispatcherQueue.TryEnqueue(() => Draw(true));
    }

    void OnTheme() => Draw(true);

    void OnSize(object sender, SizeChangedEventArgs e)
    {
        if (snap != null && (Scroller.ActualWidth - 24 >= 900) != wide) Draw(true);
    }

    void Reload()
    {
        if (reading) { again = true; return; }
        reading = true;
        lastRead = Environment.TickCount64;
        int gen = generation;
        var queue = DispatcherQueue;
        Task.Run(() =>
        {
            DockedSnapshot? s = null;
            try { s = DockedReader.Read(); } catch { }
            queue.TryEnqueue(() =>
            {
                if (gen != generation) return;
                reading = false;
                if (s != null) { snap = s; Draw(false); }
                if (again) { again = false; Reload(); }
            });
        });
    }

    void StartWatchers()
    {
        // Creating and starting the watchers takes ~150 ms of COM work (measured in CI): do it off the UI thread.
        int session = ++watchSession;
        var queue = DispatcherQueue;
        _ = Task.Run(() =>
        {
            var started = new List<DeviceWatcher>();
            try
            {
                started.Add(StartWatcher(DeviceInformation.CreateWatcher(UsbFilter, Array.Empty<string>(), DeviceInformationKind.DeviceInterface), queue));
                started.Add(StartWatcher(DeviceInformation.CreateWatcher(BtFilter, Array.Empty<string>(), DeviceInformationKind.AssociationEndpoint), queue));
                started.Add(StartWatcher(DeviceInformation.CreateWatcher(DeviceClass.AudioRender), queue));
                started.Add(StartWatcher(DeviceInformation.CreateWatcher(Windows.Devices.Display.DisplayMonitor.GetDeviceSelector()), queue));
            }
            catch { }
            queue.TryEnqueue(() =>
            {
                if (session == watchSession) watchers.AddRange(started);
                else StopAll(started);   // page already left
            });
        });
    }

    static void StopAll(IEnumerable<DeviceWatcher> list)
    {
        foreach (var w in list)
        {
            try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { }
        }
    }

    /// <summary>Wires and starts a watcher (any thread). Its events come on pool threads and are posted to <paramref name="queue"/>.</summary>
    DeviceWatcher StartWatcher(DeviceWatcher w, DispatcherQueue queue)
    {
        // The initial Added burst is the existing devices: only react once enumeration has finished.
        bool ready = false;
        void Changed(DeviceWatcher s, object o)
        {
            if (ready) queue.TryEnqueue(() => { debounce?.Stop(); debounce?.Start(); });
        }
        w.Added += (s, i) => Changed(s, i);
        w.Removed += (s, i) => Changed(s, i);
        w.Updated += (s, i) => Changed(s, i);
        w.EnumerationCompleted += (s, o) => ready = true;
        try { w.Start(); } catch { }
        return w;
    }

    // ----- drawing -----

    static DockedOptions Options() => new(Prefs.Bool("dockedInternals"), Prefs.Bool("dockedEmpty"), Prefs.Bool("dockedDisplays"), Prefs.Bool("dockedWireless"), Prefs.Bool("dockedBytes"));

    void Draw(bool force)
    {
        if (snap == null) return;
        var v = DockedBuilder.Build(snap, Options());
        bool w = Scroller.ActualWidth - 24 >= 900;
        if (!force && v.Signature == lastSig && w == wide) return;
        view = v; lastSig = v.Signature; wide = w;
        if (selected != null && !v.AllRows.Any(r => r.Id == selected && r.Detail != null)) selected = null;

        buttons.Clear();
        Body.Children.Clear();
        Body.Children.Add(DockedUi.HeaderCard(v.Header));
        Body.Children.Add(InternalsButton());
        if (v.EmptyState != null) Body.Children.Add(Note(v.EmptyState, pad: 20));

        var list = BuildList(v, inline: !w);
        if (!w)
        {
            list.MaxWidth = 600; list.HorizontalAlignment = HorizontalAlignment.Left;
            Body.Children.Add(list);
        }
        else
        {
            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(560) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(list);
            var detail = selected == null ? null : v.AllRows.First(r => r.Id == selected).Detail;
            var card = DockedUi.GroupCard(detail != null ? DockedUi.DetailPanel(detail) : Note(L("Click a row for its details.")), 0);
            card.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(card, 1); grid.Children.Add(card);
            Body.Children.Add(grid);
        }

        if (refocus != null && buttons.TryGetValue(refocus, out var b)) b.Focus(FocusState.Keyboard);
        refocus = null;
    }

    static TextBlock Note(string text, double pad = 0)
    {
        var t = DockedUi.Label(text, 11, "FuseboxSecondary", wrap: true);
        t.Margin = new Thickness(0, pad, 0, pad);
        return t;
    }

    UIElement InternalsButton()
    {
        bool on = Prefs.Bool("dockedInternals");
        var label = DockedUi.Label(on ? L("Hide hubs & internals") : L("Show hubs & internals"), 11, "FuseboxSecondary");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(DockedUi.Glyph("", 11));
        content.Children.Add(label);
        var btn = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Right, Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(8, 4, 8, 4) };
        btn.Click += (_, _) => Prefs.Set("dockedInternals", !on);   // Prefs.Changed redraws
        return btn;
    }

    StackPanel BuildList(DockedView v, bool inline)
    {
        var list = new StackPanel { Spacing = 14 };
        int index = 0;
        foreach (var g in v.Ports) list.Children.Add(DockedUi.GroupCard(Rows(g.Rows, inline), index++));
        if (v.EmptyLine != null)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0, 0, 0), Opacity = 0.75 };
            line.Children.Add(DockedUi.Glyph("", 11));
            line.Children.Add(DockedUi.Label(v.EmptyLine, 11, "FuseboxSecondary"));
            list.Children.Add(line);
        }
        if (v.Wireless.Count > 0 || v.Paired.Count > 0)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
            head.Children.Add(DockedUi.Glyph("", 10));
            head.Children.Add(DockedUi.Label(L("WIRELESS"), 10, "FuseboxSecondary"));
            list.Children.Add(head);
            foreach (var g in v.Wireless) list.Children.Add(DockedUi.GroupCard(Rows(g.Rows, inline), index++));
            if (v.Paired.Count > 0)
                list.Children.Add(Disclosure(L("Bluetooth · paired, not connected"), v.Paired.Count, Rows(v.Paired, inline), pairedOpen, o => pairedOpen = o, 4));
        }
        if (v.Other is { } other)
        {
            var body = new StackPanel { Spacing = 6 };
            body.Children.Add(Note(L("Not linked to a specific port (built in, or behind a Thunderbolt dock).")));
            body.Children.Add(Rows(other.Rows, inline));
            list.Children.Add(Disclosure(L("Other devices"), other.Rows.Count(r => !r.Dim), body, otherOpen, o => otherOpen = o, 0));
        }
        return list;
    }

    UIElement Disclosure(string title, int count, UIElement content, bool open, Action<bool> remember, double left)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(DockedUi.Label(title, 11, "FuseboxSecondary"));
        header.Children.Add(DockedUi.Number(count.ToString(), 10, "FuseboxSecondary"));
        var ex = new Expander { Header = header, Content = content, IsExpanded = open, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(left, 0, 0, 0) };
        ex.Expanding += (_, _) => remember(true);
        ex.Collapsed += (_, _) => remember(false);
        return ex;
    }

    StackPanel Rows(IReadOnlyList<TreeRow> rows, bool inline)
    {
        var style = (Style)Resources["DockedRowButton"];
        var sp = new StackPanel();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            bool hasKids = i + 1 < rows.Count && rows[i + 1].Cols.Count > r.Cols.Count;
            bool open = inline && selected == r.Id && r.Detail != null;
            sp.Children.Add(DockedUi.Row(r, selected == r.Id, hasKids, style, OnRow, open ? DockedUi.DetailPanel(r.Detail!) : null, buttons));
        }
        return sp;
    }

    void OnRow(TreeRow r)
    {
        selected = selected == r.Id ? null : r.Id;   // click again = deselect
        refocus = r.Id;
        Draw(true);
    }
}
