using Fusebox.App.Controls;
using Fusebox.Core;
using Fusebox.Core.Look;
using Fusebox.Core.Processes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Fusebox.App.Pages;

/// <summary>What the app page is opened with: the group key (or an app name), and for installed apps their name and exe.</summary>
public sealed record AppArgs(string Key, string? Name = null, string? Path = null);

/// <summary>
/// Processes tab (FEATURES.md §4): one row per app, summary, chips, search, columns by width band, sortable headers, one expandable row,
/// split pane from 1100 DIPs. Measures only while shown (ProcessMonitor.Refresh from App.Live.Updated, immediately on open).
/// Rows are built once per key and updated in place.
/// </summary>
public sealed partial class ProcessesPage : Page
{
    static readonly string[] ChipIds = ["all", "apps", "background", "macos"];
    const double SplitWidth = 1100, PaneWidth = 320, Gap = 12;

    readonly Dictionary<string, ProcRowView> rows = [];
    readonly Dictionary<string, InstalledRowView> installedRows = [];
    readonly Dictionary<string, ToggleButton> chips = [];
    readonly Dictionary<Col, FrameworkElement> headCells = [];
    readonly Dictionary<Col, TextBlock> headLabels = [];
    readonly Dictionary<Col, FontIcon> headChevrons = [];
    TextBlock nameLabel = null!;
    FontIcon nameChevron = null!;
    readonly TextBlock runningHeader = SectionHeader(Strings.L("Running")), appsHeader = SectionHeader(Strings.L("Apps"));
    readonly DispatcherQueue ui = DispatcherQueue.GetForCurrentThread();

    string filter = "all", query = "", sortId = "cpu";
    bool descending = true, showAll, split, loaded, started;
    string? expandedKey, selectedKey;
    object? pendingOpen;
    List<InstalledApp>? installed;
    string columnSignature = "";
    List<(Col Col, double Width)> columns = [];
    bool wasNarrow;

    // split pane
    Border? paneCard;
    ProcDetails? paneDetails;
    Image? paneIcon;
    FontIcon? paneFallback;
    TextBlock? paneName;

    public ProcessesPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;   // coming back from the app page keeps search, sort and the open row
        ProcUi.Flat = (Style)Resources["FlatButton"];
        Loaded += (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            Loc.Apply(this);
            BuildChips();
            BuildHeader();
            Root.SizeChanged += (_, _) => { if (ApplyWidth()) Render(); };
            ApplyWidth();
            Render();
        };
    }

    static TextBlock SectionHeader(string text)
    {
        var t = ProcUi.Text(11, "FuseboxSecondary");
        t.Text = text;
        t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        t.Margin = new Thickness(0, 4, 0, 0);
        return t;
    }

    // ---- lifecycle ----

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.NavigationMode == NavigationMode.New || !started)
        {
            // Settings are read each time the tab is opened; choices made on the page are not saved (§4.4, §4.6, §4.8).
            started = true;
            filter = Prefs.Str("procFilter");
            if (!ChipIds.Contains(filter)) filter = "all";
            sortId = Prefs.Str("procSort");
            descending = sortId != "name";
            showAll = false;
            expandedKey = selectedKey = null;
            SearchBox.Text = query = "";
            if (e.Parameter is string or AppArgs) pendingOpen = e.Parameter;
            UpdateChips();
            UpdateHeaderMarks();
        }
        App.Live.Updated += OnTick;
        ProcessMonitor.Changed += OnChanged;
        ThemeService.ThemeChanged += OnTheme;
        Prefs.Changed += OnPref;
        ProcessMonitor.Refresh(true);   // never an empty list for 2 s
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Live.Updated -= OnTick;
        ProcessMonitor.Changed -= OnChanged;
        ThemeService.ThemeChanged -= OnTheme;
        Prefs.Changed -= OnPref;
    }

    void OnTick() => ProcessMonitor.Refresh();
    void OnChanged() => Render();

    void OnTheme()
    {
        UpdateHeaderPadding();
        Render(restyle: true);
    }

    void OnPref(string key)
    {
        if (key is "procHide" or "procSmall") ui.TryEnqueue(() => { ApplyWidth(force: true); Render(); });
    }

    void Find_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
        args.Handled = true;
    }

    // ---- chips, search, header ----

    void BuildChips()
    {
        foreach (var id in ChipIds)
        {
            var t = ProcUi.Text(11, "FuseboxSecondary");
            t.Text = id switch { "all" => Strings.L("All"), "apps" => Strings.L("Apps"), "background" => Strings.L("Background"), _ => "Windows" };   // "Windows" is a name
            var b = new ToggleButton { Style = (Style)Resources["ProcChip"], Content = t, Tag = id };
            b.Click += (_, _) => { filter = id; UpdateChips(); Render(); };
            chips[id] = b;
            Chips.Children.Add(b);
        }
        UpdateChips();
    }

    void UpdateChips()
    {
        foreach (var (id, b) in chips)
        {
            bool on = id == filter;
            b.IsChecked = on;
            b.Foreground = ThemeService.Brush(on ? "FuseboxAccent" : "FuseboxSecondary");
            b.Background = on ? ThemeService.Brush("FuseboxAccentSoft") : null;
            if (b.Content is TextBlock t) t.Foreground = b.Foreground;
        }
    }

    async void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        if (q == query) return;
        query = q;
        if (query.Length > 0 && installed == null)
        {
            Render();
            installed = await ProcessMonitor.InstalledAsync();   // scanned once, in the background, on the first keystroke
        }
        Render();
    }

    void BuildHeader()
    {
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var _ in Enum.GetValues<Col>()) Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });   // the stop slot

        (nameLabel, nameChevron) = HeaderButton("name", Strings.L("Name"), Strings.L("Sort by %@", Strings.L("Name").ToLower(Strings.Culture)), left: true, column: 1);
        var defs = new (Col Col, string Sort, string Label, string Tip)[]
        {
            (Col.Cpu, "cpu", "CPU", Strings.L("Sort by %@", "CPU")),
            (Col.Gpu, "gpu", "GPU", Strings.L("Share of the graphics chip's time, like Task Manager's GPU")),
            (Col.Ram, "ram", "RAM", Strings.L("Sort by %@", "RAM")),
            (Col.Disk, "disk", "I/O", Strings.L("Data the app reads and writes per second: files (also from memory), network and devices")),   // Windows' per-app counters are not disk-only
            (Col.Threads, "threads", Strings.L("Threads"), Strings.L("Sort by %@", Strings.L("Threads").ToLower(Strings.Culture))),
            (Col.Procs, "", Strings.L("Proc."), ""),
            (Col.Running, "running", Strings.L("Running"), Strings.L("Sort by %@", Strings.L("Running").ToLower(Strings.Culture))),
        };
        foreach (var d in defs)
        {
            if (d.Sort == "")
            {
                var t = ProcUi.Text(10, "FuseboxSecondary");
                t.Text = d.Label;
                t.HorizontalAlignment = HorizontalAlignment.Right;
                t.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(t, (int)d.Col + 2);
                Header.Children.Add(t);
                headCells[d.Col] = t;
                continue;
            }
            var (tb, chev) = HeaderButton(d.Sort, d.Label, d.Tip, left: false, column: (int)d.Col + 2);
            headLabels[d.Col] = tb; headChevrons[d.Col] = chev;
        }
        UpdateHeaderPadding();
        UpdateHeaderMarks();
    }

    (TextBlock Label, FontIcon Chevron) HeaderButton(string sort, string label, string tip, bool left, int column)
    {
        var t = ProcUi.Text(10, "FuseboxSecondary");
        t.Text = label;
        var chev = ProcUi.Glyph("\uE70D", 8, "FuseboxText");
        chev.Margin = new Thickness(2, 0, 0, 0);
        chev.Visibility = Visibility.Collapsed;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(t);
        row.Children.Add(chev);
        var b = new Button { Style = ProcUi.Flat, Content = row, Padding = new Thickness(0, 2, 0, 2), Tag = sort, HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        b.Margin = new Thickness(8, 0, 0, 0);
        ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetName(b, label);
        b.Click += (_, _) => SortBy(sort);
        Grid.SetColumn(b, column);
        Header.Children.Add(b);
        if (!left) headCells[(Col)(column - 2)] = b;
        return (t, chev);
    }

    void SortBy(string id)
    {
        if (id == sortId) descending = !descending;
        else { sortId = id; descending = id != "name"; }   // name ascending, everything else descending
        UpdateHeaderMarks();
        Render();
    }

    void UpdateHeaderMarks()
    {
        if (nameLabel == null) return;
        void Mark(TextBlock label, FontIcon chev, string id)
        {
            bool on = id == sortId;
            label.Foreground = ThemeService.Brush(on ? "FuseboxText" : "FuseboxSecondary");
            chev.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) chev.Glyph = descending ? "" : "";
        }
        Mark(nameLabel, nameChevron, "name");
        var ids = new Dictionary<Col, string> { [Col.Cpu] = "cpu", [Col.Gpu] = "gpu", [Col.Ram] = "ram", [Col.Disk] = "disk", [Col.Threads] = "threads", [Col.Running] = "running" };
        foreach (var (col, label) in headLabels) Mark(label, headChevrons[col], ids[col]);
    }

    void UpdateHeaderPadding()
    {
        // 10 left and right, except 0 for the lines and plain row styles so it lines up with the row contents.
        var s = RowSpec.For(ThemeService.Current.RowStyle);
        Header.Padding = new Thickness(s.PadH, 0, s.PadH, 0);
    }

    // ---- width bands ----

    /// <summary>Recomputes split mode and the visible columns for the current width. True when anything changed.</summary>
    bool ApplyWidth(bool force = false)
    {
        double w = Root.ActualWidth;
        if (w <= 0) return false;
        bool wantSplit = w >= SplitWidth;
        double listWidth = w - 24 - (wantSplit ? PaneWidth + Gap : 0);
        var cols = ProcessList.Columns(listWidth, Prefs.List("procHide"));
        string sig = string.Join(",", cols.Select(c => $"{c.Col}:{c.Width}"));
        bool narrow = listWidth < 400;
        bool changed = force || wantSplit != split || sig != columnSignature || narrow != wasNarrow;
        if (!changed) return false;

        if (wantSplit != split)
        {
            split = wantSplit;
            PaneColumn.Width = new GridLength(split ? PaneWidth : 0);
            Pane.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            if (split) expandedKey = null;
        }
        if (narrow != wasNarrow)
        {
            wasNarrow = narrow;   // chips and search on two rows when they don't fit side by side
            Grid.SetColumn(SearchBox, narrow ? 0 : 1);
            Grid.SetRow(SearchBox, narrow ? 1 : 0);
            Grid.SetColumnSpan(SearchBox, narrow ? 2 : 1);
        }
        columns = cols;
        columnSignature = sig;
        for (int i = 0; i < Enum.GetValues<Col>().Length; i++)
        {
            var col = (Col)i;
            var match = cols.FirstOrDefault(c => c.Col == col);
            bool on = cols.Any(c => c.Col == col);
            Header.ColumnDefinitions[i + 2].Width = new GridLength(on ? match.Width + 8 : 0);
            if (headCells.TryGetValue(col, out var cell)) cell.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var r in rows.Values) r.SetColumns(cols);
        return true;
    }

    // ---- rendering ----

    void Render(bool restyle = false)
    {
        if (!loaded || ProcessMonitor.Last is not { } snap) return;
        var now = snap.Time;
        double ramTotal = App.Live.Last?.RamTotal ?? 0;
        double cpu = App.Live.Last?.Cpu ?? double.NaN, used = App.Live.Last?.RamUsed ?? double.NaN;
        Summary.Text = Strings.L("CPU %@ · RAM %@ GB · %lld items", Fmt.Pct(cpu), Fmt.One(used / 1073741824.0), snap.Groups.Count);

        if (pendingOpen != null) { var p = pendingOpen; pendingOpen = null; OpenApp(p, snap); }

        bool searching = query.Length > 0;
        string small = Prefs.Str("procSmall");
        var shown = snap.Groups.Where(g => ProcessList.MatchesFilter(g, filter)
                                           && (!searching || ProcessList.MatchesSearch(ProcessText.NameOf(g), ProcessText.ExplanationOf(g), query))).ToList();
        int hidden = 0;
        if (!searching && !showAll && small != "all")
        {
            int before = shown.Count;
            shown = shown.Where(g => !ProcessList.IsSmall(g, small)).ToList();
            hidden = before - shown.Count;
        }
        var sorted = AppGrouper.Sort(shown, ProcessList.SortOf(sortId), descending);

        // desired children of the list, in order
        var want = new List<UIElement>();
        int index = 0;
        if (searching && sorted.Count > 0) want.Add(runningHeader);
        var keep = new HashSet<string>();
        foreach (var g in sorted)
        {
            if (!rows.TryGetValue(g.Key, out var r))
            {
                rows[g.Key] = r = new ProcRowView(g.Key);
                r.SetColumns(columns);
                r.Clicked += Row_Clicked;
                r.StopClicked += Row_StopClicked;
                restyle = true;
            }
            keep.Add(g.Key);
            r.Update(g, now);
            r.Restyle(index++, restyle);
            want.Add(r.Root);
        }
        foreach (var k in rows.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            if (k == expandedKey) expandedKey = null;
            rows.Remove(k);
        }

        var instKeep = new HashSet<string>();
        if (searching && installed != null && (filter == "all" || filter == "apps"))
        {
            var hits = ProcessList.Installed(installed, snap.Groups.Select(ProcessText.NameOf), query);
            if (hits.Count > 0) want.Add(appsHeader);
            foreach (var a in hits)
            {
                if (!installedRows.TryGetValue(a.Name, out var r))
                {
                    installedRows[a.Name] = r = new InstalledRowView(a);
                    r.Clicked += Installed_Clicked;
                    restyle = true;
                }
                instKeep.Add(a.Name);
                r.TryIcon();
                r.Restyle(index++);
                want.Add(r.Root);
            }
        }
        foreach (var k in installedRows.Keys.Where(k => !instKeep.Contains(k)).ToList()) installedRows.Remove(k);

        // expansion (inline, one at a time) or selection (split)
        if (split && (selectedKey == null || !rows.ContainsKey(selectedKey))) selectedKey = sorted.FirstOrDefault()?.Key;
        int cpus = snap.Cpus;
        foreach (var (key, r) in rows)
        {
            bool open = !split && key == expandedKey;
            if (open && r.Group is { } g)
            {
                var d = r.EnsureDetails(() => NewDetails(26));
                d.Show(g, cpus, ramTotal, now);
            }
            r.SetExpanded(open);
            r.SetSelected(split && key == selectedKey);
        }
        Sync(Rows.Children, want);

        Empty.Visibility = want.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool link = !searching && small != "all" && (hidden > 0 || showAll);
        ShowAll.Visibility = link ? Visibility.Visible : Visibility.Collapsed;
        if (link)
        {
            var label = ProcUi.Text(11, "FuseboxAccent");
            label.Text = showAll ? Strings.L("Hide small items") : Strings.L("Show all (%lld)", hidden);
            if (ShowAll.Content is not TextBlock old || old.Text != label.Text) ShowAll.Content = label;
        }

        if (split) RenderPane(snap, ramTotal);
    }

    /// <summary>Makes <paramref name="children"/> equal to <paramref name="want"/> with as few moves as possible (rows jump around on a CPU sort).</summary>
    static void Sync(UIElementCollection children, List<UIElement> want)
    {
        var wantSet = new HashSet<UIElement>(want);
        for (int i = children.Count - 1; i >= 0; i--) if (!wantSet.Contains(children[i])) children.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            if (i < children.Count && ReferenceEquals(children[i], want[i])) continue;
            int cur = IndexOf(children, want[i]);
            if (cur >= 0) children.Move((uint)cur, (uint)i); else children.Insert(i, want[i]);
        }
    }

    static int IndexOf(UIElementCollection c, UIElement e)
    {
        for (int i = 0; i < c.Count; i++) if (ReferenceEquals(c[i], e)) return i;
        return -1;
    }

    ProcDetails NewDetails(double indent)
    {
        var d = new ProcDetails(indent);
        d.DetailsClicked += g => NavigateToApp(g);
        d.StopClicked += async g => await StopFlow.RunAsync(XamlRoot, g);
        return d;
    }

    // ---- split pane ----

    void RenderPane(ProcessSnapshot snap, double ramTotal)
    {
        if (paneCard == null)
        {
            paneCard = new Border { VerticalAlignment = VerticalAlignment.Top };
            var stack = new StackPanel { Spacing = 4 };
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (host, img, fb) = ProcUi.IconCell(28);
            paneIcon = img; paneFallback = fb;
            head.Children.Add(host);
            paneName = ProcUi.Text(14);
            paneName.TextWrapping = TextWrapping.Wrap;
            paneName.MaxLines = 2;
            paneName.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(paneName, 1);
            head.Children.Add(paneName);
            stack.Children.Add(head);
            paneDetails = NewDetails(0);
            stack.Children.Add(paneDetails.Root);
            paneCard.Child = stack;
            ThemedRow.Apply(paneCard, 0);
            Pane.Content = paneCard;
        }
        var g = selectedKey != null && rows.TryGetValue(selectedKey, out var r) ? r.Group : null;
        paneCard.Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
        if (g == null) return;
        ProcUi.Set(paneName!, ProcessText.NameOf(g));
        ProcUi.SetIcon(paneIcon!, paneFallback!, g);
        paneDetails!.Show(g, snap.Cpus, ramTotal, snap.Time);
    }

    // ---- interaction ----

    void Row_Clicked(ProcRowView r)
    {
        if (split) selectedKey = r.Key;                                   // split: a click only selects
        else expandedKey = expandedKey == r.Key ? null : r.Key;           // inline: one open row at a time
        Render();
    }

    async void Row_StopClicked(ProcRowView r)
    {
        if (r.Group is { } g) await StopFlow.RunAsync(XamlRoot, g);
    }

    void Installed_Clicked(InstalledRowView r) => Frame.Navigate(typeof(AppPage), new AppArgs("installed:" + r.App.Name, r.App.Name, r.App.Path));

    void NavigateToApp(AppGroup g) => Frame.Navigate(typeof(AppPage), new AppArgs(g.Key, g.Name, g.ExePath));

    /// <summary>"Open the app page for this app" from another page (Live top app, peak notification): a key, a name or ready-made args.</summary>
    void OpenApp(object target, ProcessSnapshot snap)
    {
        var args = target as AppArgs;
        if (args == null)
        {
            string s = (string)target;
            var g = snap.Groups.FirstOrDefault(x => x.Key == s) ?? snap.Groups.FirstOrDefault(x => string.Equals(x.Name, s, StringComparison.CurrentCultureIgnoreCase));
            args = g != null ? new AppArgs(g.Key, g.Name, g.ExePath) : new AppArgs(s, s, null);
        }
        // Not inside OnNavigatedTo: MainWindow.Open clears the back stack right after navigating here.
        ui.TryEnqueue(() => Frame.Navigate(typeof(AppPage), args));
    }

    void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        showAll = !showAll;   // not saved: reopening the tab starts from the setting again
        Render();
    }
}
