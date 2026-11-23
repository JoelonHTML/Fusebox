using Fusebox.App.Controls;
using Fusebox.Core.Look;
using Fusebox.Core.Network;
using Fusebox.Platform.Network;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>
/// Network (§6): three cards (Connection, Wi-Fi or cable, Speed test) and below them the Devices section (toolbar, column header pinned while the rows scroll) of a LAN scan.
/// Nothing runs while the page is hidden: pings, scans and timers start in OnNavigatedTo and stop in OnNavigatedFrom. Cloudflare only after a click.
/// Parts: NetworkPage.xaml.cs (page, toolbar, header, pinned header), .Cards.cs (card frames, ⓘ, hover explanations), .Panel.cs (Connection + Wi-Fi cards), .Speed.cs (Speed test card, public IP), .Devices.cs (scan, rows).
/// </summary>
public sealed partial class NetworkPage : Page
{
    // ---- state ----
    bool visible, built;
    NetBand band = NetBand.Kind;
    string hide = "";
    LocalNet? net;
    ScanState scan = new();
    DeviceStore store = new();
    List<NetDevice> all = [];
    NetFilter filter = NetFilter.All;
    NetCol sortCol = NetCol.Ip;
    bool ascending = true, scanning;
    string search = "";
    string? customRange;                 // text of the custom range; null = this network (resets when the page is left)
    List<uint>? customHosts;
    DateTime scanStart;
    double expected = 8;
    CancellationTokenSource? scanCts;
    DispatcherQueueTimer? listTimer, progressTimer;

    // ---- toolbar parts ----
    SelectorBar filterBar = null!;
    ComboBox filterBox = null!;
    TextBox searchBox = null!;
    Button searchClear = null!;
    TextBlock countText = null!, phaseText = null!;
    bool pinned;
    ProgressBar scanBar = null!;
    Button rangePill = null!, bell = null!, scanPill = null!;
    Flyout rangeFlyout = null!;
    TextBox rangeBox = null!;
    TextBlock rangeHelp = null!;
    StackPanel rangeHost = null!;
    Button rangeAuto = null!;
    bool settingFilter;

    public NetworkPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Build();
    }

    // ---- lifetime ----

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        visible = true;
        store = DeviceStore.Load();
        hide = Prefs.Str("netHide");
        // nothing but the recent list survives a visit: filter, search, sort, range and the open row start fresh
        filter = NetFilter.All; search = ""; sortCol = NetCol.Ip; ascending = true; openId = null; customRange = null; customHosts = null;
        if (built) searchBox.Text = "";
        SetPinned(false);
        Prefs.Changed += OnPref;
        ThemeService.ThemeChanged += OnTheme;
        listTimer = DispatcherQueue.CreateTimer();
        listTimer.Interval = TimeSpan.FromMilliseconds(300);
        listTimer.IsRepeating = false;
        listTimer.Tick += (_, _) => { if (visible) RenderList(); };
        progressTimer = DispatcherQueue.CreateTimer();
        progressTimer.Interval = TimeSpan.FromMilliseconds(250);
        progressTimer.Tick += (_, _) => { if (visible && scanning) scanBar.Value = Math.Min(1, (DateTime.UtcNow - scanStart).TotalSeconds / expected); };
        if (built) Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        visible = false;
        Prefs.Changed -= OnPref;
        ThemeService.ThemeChanged -= OnTheme;
        listTimer?.Stop(); progressTimer?.Stop();
        StopPanel();
        StopSpeed();
        scanCts?.Cancel();
        portsCts?.Cancel();
        publicIp = null; publicIpState = null; ipCts?.Cancel();
    }

    /// <summary>Starts everything once the UI exists and the page is shown: panel (connection + quality + live), scan (auto), "find the problem" asked by the tray.</summary>
    void Start()
    {
        ApplyBand(force: true);
        StartPanel();
        RenderToolbar();
        RenderList();
        if (Prefs.Bool("netAutoScan")) StartScan();
        else RenderList();
        if (Prefs.Bool("netFindNow")) { Prefs.Set("netFindNow", false); FindProblem(); }
    }

    void UI(Action a) => DispatcherQueue.TryEnqueue(() => { if (visible) a(); });

    void OnTheme() { if (!visible || !built) return; BuildHeader(); BuildCards(); RenderCards(); RenderList(force: true); RenderToolbar(); if (pinned) PinnedHost.Background = PinBackdrop(); else UpdatePin(); }

    void OnPref(string key) => UI(() =>
    {
        switch (key)
        {
            case "netHide": hide = Prefs.Str("netHide"); ApplyBand(force: true); RenderList(force: true); break;
            case "pingHost": if (net != null) StartQuality(); break;
            case "speedMB": RenderSpeed(); break;
            case "notifyNewDevice": RenderToolbar(); break;
            case "netFindNow": if (Prefs.Bool("netFindNow")) { Prefs.Set("netFindNow", false); FindProblem(); } break;
        }
    });

    // ---- small builders (all text through Strings.L, all colors through the theme brushes) ----

    static Brush B(string key) => ThemeService.Brush(key);
    static bool Blocks => ThemeService.Current.RowStyle == RowStyle.Blocks;
    /// <summary>Verdict colors: good/warn, white on Color blocks.</summary>
    static Brush OkBrush(bool? ok) => Blocks ? B("FuseboxOnRow") : ok == false ? B("FuseboxWarn") : ok == true ? B("FuseboxGood") : B("FuseboxOnRowSecondary");
    static Brush WarnBrush() => Blocks ? B("FuseboxOnRow") : B("FuseboxWarn");

    static TextBlock Lbl(string text, double pt = 11, string brush = "FuseboxOnRowSecondary", bool wrap = true)
    {
        var t = new TextBlock { Text = text, Foreground = B(brush), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        Look.Label(t, pt);
        return t;
    }

    static TextBlock Num(string text, double pt, string brush = "FuseboxOnRow")
    {
        var t = new TextBlock { Text = text, Foreground = B(brush) };
        Look.Number(t, pt);
        return t;
    }

    static FontIcon Icon(string glyph, double size = 11, string brush = "FuseboxOnRowSecondary") =>
        new() { Glyph = glyph, FontSize = size, Foreground = B(brush) };

    /// <summary>Icon in a 14 DIPs wide frame + wrapping text (never truncated).</summary>
    static Grid Line(string glyph, string text, Brush? fg = null, string textBrush = "FuseboxOnRowSecondary")
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ic = Icon(glyph, 11); ic.VerticalAlignment = VerticalAlignment.Top; ic.Margin = new Thickness(0, 1, 0, 0);
        var t = Lbl(text, 11, textBrush);
        if (fg != null) { ic.Foreground = fg; t.Foreground = fg; }
        Grid.SetColumn(t, 1);
        g.Children.Add(ic); g.Children.Add(t);
        return g;
    }

    /// <summary>Gives a Button the look of one state set: the default template reads these resources, so no custom template is needed.</summary>
    static void Look3(Button b, Brush bg, Brush fg, Brush? bgHover = null, Brush? bgDisabled = null, Brush? fgDisabled = null)
    {
        b.Background = bg; b.Foreground = fg; b.BorderThickness = new Thickness(0); b.MinHeight = 0; b.MinWidth = 0;
        var none = new SolidColorBrush(Colors.Transparent);
        foreach (var s in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            b.Resources["ButtonBackground" + s] = s == "PointerOver" || s == "Pressed" ? bgHover ?? bg : s == "Disabled" ? bgDisabled ?? bg : bg;
            b.Resources["ButtonForeground" + s] = s == "Disabled" ? fgDisabled ?? fg : fg;
            b.Resources["ButtonBorderBrush" + s] = none;
        }
    }

    /// <summary>NetPill (§6.2): icon + title, accent text on accent at 15 %, radius 6, padding 7,3, no hover effect, dimmed when disabled. Accent = white on Color blocks.</summary>
    Button Pill(string glyph, string text, string? tip = null, RoutedEventHandler? click = null)
    {
        var b = new Button { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 3, 7, 3) };
        Look3(b, B("FuseboxGraphFill"), B("FuseboxGraph"), null, B("FuseboxGraphFill"), B("FuseboxSecondary"));
        SetPill(b, glyph, text);
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        if (click != null) b.Click += click;
        return b;
    }

    static void SetPill(Button b, string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (glyph.Length > 0) row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        Look.Label(t, 11);
        row.Children.Add(t);
        b.Content = row;
    }

    /// <summary>Plain button: no bezel, the whole area is the hit target, faint hover.</summary>
    Button Plain(UIElement content, string? tip, double w, double h, RoutedEventHandler click)
    {
        var b = new Button { Content = content, Width = w, Height = h, Padding = new Thickness(0), CornerRadius = new CornerRadius(4) };
        Look3(b, new SolidColorBrush(Colors.Transparent), B("FuseboxText"), B("FuseboxHover"));
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        b.Click += click;
        return b;
    }

    static void Open(string uri) { try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(uri)); } catch { } }

    // ---- build: toolbar, blocked hint, column header (once) ----

    void Build()
    {
        if (built) return;
        built = true;
        BuildCards();
        BuildToolbar();
        BuildHint();
        var title = Lbl(L("Devices"), 13, "FuseboxText", wrap: false); title.VerticalAlignment = VerticalAlignment.Center;
        countText.VerticalAlignment = VerticalAlignment.Center;
        TitleRow.Children.Add(title); TitleRow.Children.Add(countText);
        DevicesHeader.SizeChanged += (_, _) => { if (pinned) HeaderSlot.Height = DevicesHeader.ActualHeight; };
        EmptyText.Foreground = B("FuseboxSecondary");
        Look.Label(EmptyText, 12);
        if (visible) Start();
    }

    void BuildToolbar()
    {
        // filter: segmented (SelectorBar) when wide, a dropdown when narrow; not persisted
        filterBar = new SelectorBar();
        filterBox = new ComboBox { MinWidth = 84, VerticalAlignment = VerticalAlignment.Center };
        foreach (var f in Enum.GetValues<NetFilter>())
        {
            filterBar.Items.Add(new SelectorBarItem { Text = NetView.FilterTitle(f), Tag = f });
            filterBox.Items.Add(NetView.FilterTitle(f));
        }
        filterBar.SelectedItem = filterBar.Items[0];
        filterBox.SelectedIndex = 0;
        filterBar.SelectionChanged += (s, _) =>
        {
            if (settingFilter || s.SelectedItem is not SelectorBarItem { Tag: NetFilter f }) return;
            filter = f; RenderList();
        };
        filterBox.SelectionChanged += (_, _) =>
        {
            if (settingFilter || filterBox.SelectedIndex < 0) return;
            filter = (NetFilter)filterBox.SelectedIndex; RenderList();
        };

        // search field: line at 50 %, radius 6
        searchBox = new TextBox { PlaceholderText = L("Search"), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), MinHeight = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            searchBox.Resources[k] = new SolidColorBrush(Colors.Transparent);
        searchBox.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        foreach (var k in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) searchBox.Resources[k] = B("FuseboxText");
        foreach (var k in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" }) searchBox.Resources[k] = B("FuseboxSecondary");
        searchBox.Foreground = B("FuseboxText");
        searchBox.FontSize = ThemeService.Current.Size(12);
        searchBox.TextChanged += (_, _) => { search = searchBox.Text; searchClear.Visibility = search.Length > 0 ? Visibility.Visible : Visibility.Collapsed; RenderList(); };
        searchClear = Plain(Icon("", 10, "FuseboxSecondary"), L("Clear search"), 18, 18, (_, _) => searchBox.Text = "");
        searchClear.Visibility = Visibility.Collapsed;
        var searchGrid = new Grid { MinHeight = 24 };
        var searchBg = new Border { Background = B("FuseboxLine"), Opacity = 0.5, CornerRadius = new CornerRadius(6) };
        var inner = new Grid { ColumnSpacing = 4, Padding = new Thickness(7, 3, 7, 3) };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mag = Icon("", 11, "FuseboxSecondary"); mag.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(searchBox, 1); Grid.SetColumn(searchClear, 2);
        inner.Children.Add(mag); inner.Children.Add(searchBox); inner.Children.Add(searchClear);
        searchGrid.Children.Add(searchBg); searchGrid.Children.Add(inner);
        ToolTipService.SetToolTip(searchGrid, L("Filter on name, IP, MAC address or maker"));

        countText = Lbl("", 11, "FuseboxSecondary", wrap: false); countText.VerticalAlignment = VerticalAlignment.Center;
        phaseText = Lbl("", 10, "FuseboxSecondary", wrap: false); phaseText.VerticalAlignment = VerticalAlignment.Center;
        scanBar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 70, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        rangePill = Pill("", L("This network"));
        BuildRangeFlyout();
        rangePill.Flyout = rangeFlyout;
        bell = Plain(Icon("", 12), null, 22, 20, (_, _) => Prefs.Set("notifyNewDevice", !Prefs.Bool("notifyNewDevice")));
        scanPill = Pill("", L("Scan"), click: (_, _) => { if (scanning) scanCts?.Cancel(); else StartScan(); });

        // columns: filter | search (70…170) | spacer | phase | progress | range | bell | scan   (the count sits in the Devices title row)
        var cols = new GridLength[] { GridLength.Auto, new(1, GridUnitType.Star), new(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto };
        for (int i = 0; i < cols.Length; i++)
        {
            var d = new ColumnDefinition { Width = cols[i] };
            if (i == 1) { d.MinWidth = 70; d.MaxWidth = 170; }
            Toolbar.ColumnDefinitions.Add(d);
        }
        var parts = new FrameworkElement[] { filterBar, searchGrid, new Border(), phaseText, scanBar, rangePill, bell, scanPill };
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] is FrameworkElement fe && i != 1) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(parts[i], i);
            Toolbar.Children.Add(parts[i]);
        }
        Grid.SetColumn(filterBox, 0);
        Toolbar.Children.Add(filterBox);
    }

    void RenderToolbar()
    {
        if (!built) return;
        bool wide = band >= NetBand.Kind;
        filterBar.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        filterBox.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        settingFilter = true;
        filterBar.SelectedItem = filterBar.Items[(int)filter]; filterBox.SelectedIndex = (int)filter;
        settingFilter = false;
        countText.Text = NetView.CountText(all);
        searchBox.FontSize = ThemeService.Current.Size(12);
        phaseText.Visibility = wide && scanning ? Visibility.Visible : Visibility.Collapsed;
        scanBar.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        scanBar.Width = wide ? 70 : 36;
        SetPill(rangePill, "", wide ? customRange ?? L("This network") : L("Range"));
        if (!wide) { if (rangePill.Content is StackPanel sp && sp.Children.Count > 1) sp.Children[1].Visibility = Visibility.Collapsed; }
        ToolTipService.SetToolTip(rangePill, L("Scan range: %@. Click to type a range.", customRange ?? L("this network (found automatically)")));
        bool on = Prefs.Bool("notifyNewDevice");
        ((FontIcon)bell.Content).Foreground = B(on ? "FuseboxAccent" : "FuseboxSecondary");
        ToolTipService.SetToolTip(bell, on ? L("Notifying you when a new device shows up (while Fusebox scans). Click to turn off.")
            : L("Get a notification when a new device shows up on your network (while this page scans)"));
        SetPill(scanPill, scanning ? "" : "", scanning ? L("Stop") : L("Scan"));
        ToolTipService.SetToolTip(scanPill, scanning ? L("Stop scanning (keeps what was found so far)") : L("Look for devices in the scan range"));
    }

    // ---- scan range popover (§6.7) ----

    void BuildRangeFlyout()
    {
        rangeFlyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        presenter.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        rangeFlyout.FlyoutPresenterStyle = presenter;
        rangeHost = new StackPanel { Width = 320, Padding = new Thickness(12), Spacing = 8 };
        rangeFlyout.Content = rangeHost;
        rangeFlyout.Opening += (_, _) => FillRange();
    }

    void FillRange()
    {
        rangeHost.Children.Clear();
        var title = Lbl(L("Scan range"), 13, "FuseboxText"); rangeHost.Children.Add(title);

        var auto = new StackPanel { Spacing = 1 };
        auto.Children.Add(Lbl(L("This network (auto)"), 12, "FuseboxText"));
        if (net != null && NetAddress.Hosts(net.Ip, net.Prefix) is { Count: > 0 } h)
            auto.Children.Add(Lbl($"{NetAddress.IpString(h[0])} – {NetAddress.IpString(h[^1])}", 10, "FuseboxSecondary"));
        var autoRow = Line(customRange == null ? "" : "", "");
        autoRow.Children.RemoveAt(1);
        Grid.SetColumn(auto, 1); autoRow.Children.Add(auto);
        rangeAuto = Plain(autoRow, null, double.NaN, double.NaN, (_, _) => ApplyRange(null));
        rangeAuto.HorizontalAlignment = HorizontalAlignment.Stretch; rangeAuto.HorizontalContentAlignment = HorizontalAlignment.Left;
        rangeHost.Children.Add(rangeAuto);

        rangeBox = new TextBox { PlaceholderText = L("192.168.1.0/24 or 192.168.1.10-80"), Text = customRange ?? "" };
        rangeHelp = Lbl("", 10, "FuseboxSecondary");
        var go = Pill("", L("Scan"));
        void Check()
        {
            var t = rangeBox.Text.Trim();
            var r = t.Length == 0 ? default : NetAddress.ParseRange(t);
            go.IsEnabled = t.Length > 0 && r.Ok;
            rangeHelp.Text = NetView.RangeHelp(t, net == null ? null : new ScanInfo(net.Ip, net.Prefix, net.Gateway, net.Mac, ""));
            rangeHelp.Foreground = t.Length > 0 && !r.Ok ? B("FuseboxDanger") : B("FuseboxSecondary");
        }
        rangeBox.TextChanged += (_, _) => Check();
        rangeBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter && go.IsEnabled) { e.Handled = true; ApplyRange(rangeBox.Text.Trim()); } };
        go.Click += (_, _) => ApplyRange(rangeBox.Text.Trim());
        var entry = new Grid { ColumnSpacing = 6 };
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(go, 1); go.VerticalAlignment = VerticalAlignment.Center;
        entry.Children.Add(rangeBox); entry.Children.Add(go);
        rangeHost.Children.Add(entry); rangeHost.Children.Add(rangeHelp);
        Check();

        var recent = Prefs.List("netRecentRanges");
        if (recent.Count > 0)
        {
            var hd = Lbl(L("Recent"), 10, "FuseboxSecondary"); hd.Margin = new Thickness(0, 2, 0, 0);
            rangeHost.Children.Add(hd);
            foreach (var r in recent.Take(5))
            {
                var row = Line(customRange == r ? "" : "", r, null, "FuseboxText");
                var rb = Plain(row, null, double.NaN, double.NaN, (_, _) => ApplyRange(r));
                rb.HorizontalAlignment = HorizontalAlignment.Stretch; rb.HorizontalContentAlignment = HorizontalAlignment.Left;
                rangeHost.Children.Add(rb);
            }
        }
    }

    /// <summary>null = back to this network. A valid range goes to the top of the recent list; then rescan at once.</summary>
    void ApplyRange(string? text)
    {
        rangeFlyout.Hide();
        var t = text?.Trim() ?? "";
        var r = t.Length == 0 ? default : NetAddress.ParseRange(t);
        if (t.Length > 0 && r.Ok)
        {
            customRange = t;
            customHosts = Enumerable.Range(0, r.Count).Select(i => r.First + (uint)i).ToList();
            Prefs.SetList("netRecentRanges", NetView.PushRecent(Prefs.List("netRecentRanges"), t));
        }
        else { customRange = null; customHosts = null; }
        scanCts?.Cancel();
        StartScan();
    }

    // ---- blocked hint (§6.9): Windows has no Local Network permission; a firewall or security suite can block the scan ----

    void BuildHint()
    {
        BlockedHint.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        BlockedHint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        BlockedHint.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ic = Icon("", 11, "FuseboxWarn"); ic.VerticalAlignment = VerticalAlignment.Top; ic.Margin = new Thickness(0, 2, 0, 0);
        var t = Lbl(L("Windows is blocking the scan. Allow Fusebox in Windows Security under Firewall & network protection, then press Scan."), 11, "FuseboxWarn");
        var open = Plain(Lbl(L("Open"), 11, "FuseboxAccent", wrap: false), null, double.NaN, double.NaN, (_, _) => Open("ms-settings:windowsdefender"));
        open.Padding = new Thickness(4, 0, 4, 0);
        Grid.SetColumn(t, 1); Grid.SetColumn(open, 2);
        BlockedHint.Children.Add(ic); BlockedHint.Children.Add(t); BlockedHint.Children.Add(open);
    }

    // ---- width bands (§6.1) ----

    void RootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyBand();
        LayoutCards();
    }

    void ApplyBand(bool force = false)
    {
        if (!built) return;
        var nb = NetView.Band(Root.ActualWidth - 24);   // the page padding (12 + 12) is not list width
        if (nb == band && !force) return;
        band = nb;
        RenderToolbar();
        BuildHeader();
        foreach (var r in rows.Values) r.Layout(band, hide);
    }

    // ---- column header + sorting (§6.8) ----

    void BuildHeader()
    {
        HeaderRow.Children.Clear(); HeaderRow.ColumnDefinitions.Clear();
        var spec = RowSpec.For(ThemeService.Current.RowStyle);
        HeaderRow.Margin = new Thickness(spec.PadH, 0, spec.PadH, 0);
        void Col(NetCol c, string title, string? tip, bool right)
        {
            if (!NetView.Shows(band, hide, c)) return;
            bool star = c == NetCol.Name;
            HeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = star ? new GridLength(1, GridUnitType.Star) : new GridLength(NetView.Width(c)) });
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
            bool active = sortCol == c;
            var t = new TextBlock { Text = title, Foreground = B(active ? "FuseboxText" : "FuseboxSecondary") };
            Look.Label(t, 10);
            sp.Children.Add(t);
            if (active) sp.Children.Add(new FontIcon { Glyph = ascending ? "" : "", FontSize = 8, Foreground = B("FuseboxText"), VerticalAlignment = VerticalAlignment.Center });
            var b = Plain(sp, tip, double.NaN, double.NaN, (_, _) =>
            {
                if (sortCol == c) ascending = !ascending; else { sortCol = c; ascending = true; }
                BuildHeader(); RenderList();
            });
            b.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            b.HorizontalContentAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            if (c == NetCol.Name) b.Margin = new Thickness(26, 0, 0, 0);
            Grid.SetColumn(b, HeaderRow.ColumnDefinitions.Count - 1);
            HeaderRow.Children.Add(b);
        }
        Col(NetCol.Name, L("Name"), null, false);
        Col(NetCol.Ip, "IP", null, false);
        Col(NetCol.Mac, "MAC", L("Hardware address"), false);
        Col(NetCol.Vendor, L("Vendor"), null, false);
        Col(NetCol.Kind, L("Type"), null, false);
        Col(NetCol.Ms, "ms", L("Reply time in milliseconds"), true);
    }

    // ---- pinned Devices header ----
    // The real header (title, toolbar, hint, column header) lives in HeaderSlot. Once it has scrolled out of view it moves into PinnedHost at the top of the page
    // (the slot keeps its height, so nothing jumps); scrolling back moves it home. Only on scroll, never per frame.

    /// <summary>A solid backdrop so rows don't show through (Liquid Glass has no solid bg: its glass fill).</summary>
    static Brush PinBackdrop() => B(ThemeService.Current.Bg == null ? "FuseboxGlassFill" : "FuseboxBg");

    void PageScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdatePin();

    void UpdatePin()
    {
        if (!built) return;
        double top;
        try { top = HeaderSlot.TransformToVisual(PageBody).TransformPoint(new Point(0, 0)).Y; } catch { return; }
        SetPinned(PageScroll.VerticalOffset > top + 0.5);
    }

    void SetPinned(bool on)
    {
        if (on == pinned) return;
        pinned = on;
        if (on)
        {
            HeaderSlot.Height = DevicesHeader.ActualHeight;
            HeaderSlot.Children.Remove(DevicesHeader);
            PinnedHost.Child = DevicesHeader;
            PinnedHost.Background = PinBackdrop();
            PinnedHost.Visibility = Visibility.Visible;
        }
        else
        {
            PinnedHost.Visibility = Visibility.Collapsed;
            PinnedHost.Child = null;
            HeaderSlot.Children.Add(DevicesHeader);
            HeaderSlot.Height = double.NaN;
        }
    }
}
