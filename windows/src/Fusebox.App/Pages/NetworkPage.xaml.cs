using Fusebox.Core.Network;
using Fusebox.Platform.Network;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>
/// Network: connection + Wi-Fi (refreshed every 5 s while visible), "Find the problem", speed test and LAN scan (only on click).
/// Everything cancels when the page is left; nothing runs while hidden.
/// </summary>
public sealed partial class NetworkPage : Page
{
    readonly List<NetDevice> devices = new();
    CancellationTokenSource? scanCts, speedCts, findCts, portsCts, metaCts;
    DispatcherQueueTimer? signalTimer, listTimer;
    NetDevice? selected;
    LocalNet? net;
    bool visible, rebuilding, refreshing;

    public NetworkPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
    }

    // ---- lifetime: start in OnNavigatedTo, stop in OnNavigatedFrom ----

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        visible = true;
        signalTimer = DispatcherQueue.CreateTimer();
        signalTimer.Interval = TimeSpan.FromSeconds(5);
        signalTimer.Tick += (_, _) => _ = RefreshAsync();
        signalTimer.Start();
        listTimer = DispatcherQueue.CreateTimer();
        listTimer.Interval = TimeSpan.FromMilliseconds(300);
        listTimer.IsRepeating = false;
        listTimer.Tick += (_, _) => RebuildList();
        _ = RefreshAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        visible = false;
        signalTimer?.Stop(); listTimer?.Stop();
        foreach (var c in new[] { scanCts, speedCts, findCts, portsCts, metaCts }) c?.Cancel();
    }

    void UI(Action a) => DispatcherQueue.TryEnqueue(() => { if (visible) a(); });

    Brush Pick(string key, byte r, byte g, byte b) =>
        Resources.TryGetValue(key, out var v) && v is Brush br ? br : new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
    Brush Good => Pick("GoodBrush", 0x30, 0xA4, 0x6C);
    Brush Bad => Pick("BadBrush", 0xE5, 0x48, 0x4D);
    Brush Warn => Pick("WarnBrush", 0xF7, 0xB5, 0x00);

    static TextBlock T(string text, double opacity = 1, bool bold = false) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, IsTextSelectionEnabled = true };

    static string MsText(double? ms) => ms is { } m ? (m < 1 ? "<1" : ((int)Math.Round(m)).ToString()) : "–";
    static string Mbit(double m) => (int)Math.Round(m) + " Mbit/s";

    // ---- connection + Wi-Fi ----

    async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var (n, w) = await Task.Run(() => (NetProbe.Current(), Wifi.Read()));
            if (!visible) return;
            net = n;
            ShowConnection(n);
            ShowWifi(w);
        }
        catch { }
        finally { refreshing = false; }
    }

    void ShowConnection(LocalNet? n)
    {
        if (n == null) { ConnText.Text = L("Not connected to a network"); return; }
        var lines = new List<string>
        {
            L("This PC %@ on %@", NetAddress.IpString(n.Ip), n.IsWifi ? "Wi-Fi" : "Ethernet") + $" ({n.Name})",
            L("Router") + ": " + (n.Gateway is { } g ? NetAddress.IpString(g) : L("unknown")),
            "DNS: " + (n.Dns.Length == 0 ? L("unknown") : string.Join(", ", n.Dns.Take(3))),
        };
        ConnText.Text = string.Join("\n", lines);
    }

    void ShowWifi(WifiInfo? w)
    {
        WifiCard.Visibility = w == null ? Visibility.Collapsed : Visibility.Visible;
        if (w == null) return;
        if (!w.Connected) { WifiText.Text = L("Not connected to a network"); WifiHint.Visibility = WifiLocationButton.Visibility = Visibility.Collapsed; return; }
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(w.Ssid)) lines.Add(L("Network name") + ": " + w.Ssid);
        if (w.Rssi is { } r) lines.Add(L("Signal") + $": {r} dBm ({WifiMath.Word(r)})" + (w.SignalPercent is { } q ? $" · {q}%" : ""));
        else if (w.SignalPercent is { } q2) lines.Add(L("Signal") + $": {q2}%");
        if (w.RxMbps is > 0) lines.Add(L("Link speed") + $": {w.RxMbps} Mbit/s" + (w.TxMbps is > 0 && w.TxMbps != w.RxMbps ? $" ↓ · {w.TxMbps} Mbit/s ↑" : ""));
        if (w.Channel is { } ch) lines.Add(L("Channel") + ": " + string.Join(" · ", new[] { ch.ToString(), w.Band }.Where(x => x != null)));
        if (w.Phy != null) lines.Add(L("Standard") + ": " + w.Phy);
        WifiText.Text = string.Join("\n", lines);
        // Windows 11 24H2+ hides the name until the user allows location access
        WifiHint.Visibility = WifiLocationButton.Visibility = w.NameHidden ? Visibility.Visible : Visibility.Collapsed;
    }

    void WifiLocationClick(object sender, RoutedEventArgs e) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-location"));

    // ---- find the problem ----

    async void FindClick(object sender, RoutedEventArgs e)
    {
        findCts?.Cancel();
        var cts = findCts = new CancellationTokenSource();
        FindButton.IsEnabled = false; FindRing.IsActive = true;
        StepsPanel.Children.Clear(); FindNumbers.Visibility = Visibility.Collapsed;
        FindVerdict.Text = L("Testing the connection…"); FindVerdict.Visibility = Visibility.Visible; FindVerdict.ClearValue(TextBlock.ForegroundProperty);
        try
        {
            var (finding, steps, _) = await Task.Run(() => Troubleshooter.RunAsync(cts.Token), cts.Token);
            if (!visible) return;
            FindVerdict.Text = finding.Text;
            FindVerdict.Foreground = finding.Ok == true ? Good : finding.Ok == false ? Bad : Warn;
            FindNumbers.Text = finding.Numbers; FindNumbers.Visibility = finding.Numbers.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var s in steps) StepsPanel.Children.Add(StepRow(s));
            FindButton.Content = L("Check again");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FindVerdict.Text = ex.Message; }
        finally { if (findCts == cts) { FindButton.IsEnabled = true; FindRing.IsActive = false; } }
    }

    UIElement StepRow(DiagStep s)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = s.Ok == true ? "✓" : s.Ok == false ? "✗" : "–", Width = 16, Foreground = s.Ok == true ? Good : s.Ok == false ? Bad : Warn });
        var col = new StackPanel();
        col.Children.Add(T(s.Title + (s.Detail.Length > 0 ? " · " + s.Detail : "")));
        if (s.Advice != null) col.Children.Add(T(s.Advice, 0.75));
        row.Children.Add(col);
        return row;
    }

    // ---- speed test (only on click) ----

    void QuickClick(object sender, RoutedEventArgs e) => RunSpeed(SpeedKind.Quick);
    void FullClick(object sender, RoutedEventArgs e) => RunSpeed(SpeedKind.Full);
    void SpeedCancelClick(object sender, RoutedEventArgs e) => speedCts?.Cancel();

    async void RunSpeed(SpeedKind kind)
    {
        if (speedCts != null) return;
        var cts = speedCts = new CancellationTokenSource();
        QuickButton.IsEnabled = FullButton.IsEnabled = false;
        SpeedCancelButton.Visibility = SpeedBar.Visibility = SpeedPhase.Visibility = Visibility.Visible;
        SpeedResultText.Visibility = Visibility.Collapsed; SpeedBar.Value = 0;
        try
        {
            var gw = net?.Gateway;
            var routerT = gw is { } g ? NetProbe.PingSeries(NetProbe.ToAddress(g), 5, 100, cts.Token) : Task.FromResult<PingStats>(null!);
            var inetT = NetProbe.PingSeries(System.Net.IPAddress.Parse("1.1.1.1"), 5, 100, cts.Token);
            var router = await routerT; var inet = await inetT;
            var r = await Task.Run(() => SpeedTest.RunAsync(kind, (phase, live, p) => UI(() =>
            {
                SpeedPhase.Text = phase + (live is { } l ? " · " + Mbit(l) : "");
                SpeedBar.Value = p;
            }), cts.Token), cts.Token);
            if (!visible) return;
            ShowSpeed(r, router, inet);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SpeedResultText.Text = L("Speed test failed: %@", ex.Message); SpeedResultText.Visibility = Visibility.Visible; }
        finally
        {
            speedCts = null;
            QuickButton.IsEnabled = FullButton.IsEnabled = true;
            SpeedCancelButton.Visibility = SpeedBar.Visibility = SpeedPhase.Visibility = Visibility.Collapsed;
        }
    }

    void ShowSpeed(SpeedResult r, PingStats? router, PingStats inet)
    {
        var lines = new List<string>
        {
            L("Download") + ": " + Mbit(r.Down) + (r.DownPeak is { } dp ? L(" · top %@", Mbit(dp)) : ""),
            L("Upload") + ": " + Mbit(r.Up) + (r.UpPeak is { } up ? L(" · top %@", Mbit(up)) : ""),
            L("Delay") + ": " + (r.Ping is { } p ? MsText(p) + " ms" + (r.LoadedPing is { } lp ? L(" · busy %@ ms", MsText(lp)) : "") + (r.Jitter is { } j ? L(" · jitter %@", MsText(j)) : "") : "–"),
        };
        if (r.Variation is { } v) lines.Add(L("Stability") + ": " + SpeedMath.StabilityText(v));
        if (r.Ping is { } idle && r.LoadedPing is { } loaded) lines.Add(SpeedMath.LoadVerdict(idle, loaded).Text);
        // router vs. internet: where is the delay?
        lines.Add(L("Router") + ": " + (router is { Got: > 0 } ? MsText(router.Avg) + " ms" : "–") + " · Internet: " + (inet.Got > 0 ? MsText(inet.Avg) + " ms" : "–"));
        SpeedResultText.Text = string.Join("\n", lines);
        SpeedResultText.Visibility = Visibility.Visible;
    }

    async void IpClick(object sender, RoutedEventArgs e)
    {
        metaCts?.Cancel();
        var cts = metaCts = new CancellationTokenSource();
        IpButton.IsEnabled = false;
        IpText.Text = L("Asking Cloudflare…"); IpText.Visibility = Visibility.Visible;
        try
        {
            var ip = await SpeedTest.MetaAsync(cts.Token);
            if (!visible) return;
            IpText.Text = ip == null ? L("Couldn't reach Cloudflare")
                : L("Public IP") + ": " + ip.Ip + (ip.Isp != null ? "\n" + L("Provider") + ": " + ip.Isp : "") + (ip.Place != null ? "\n" + L("Location") + ": " + ip.Place : "");
        }
        catch (OperationCanceledException) { }
        catch { if (visible) IpText.Text = L("Couldn't reach Cloudflare"); }
        finally { IpButton.IsEnabled = true; }
    }

    // ---- LAN scan ----

    void RangeChanged(object sender, TextChangedEventArgs e)
    {
        var t = RangeBox.Text.Trim();
        if (t.Length == 0) { RangeNote.Visibility = Visibility.Collapsed; return; }
        var r = NetAddress.ParseRange(t);
        RangeNote.Text = r.Ok ? (r.Count == 1 ? L("1 address") : L("%lld addresses", r.Count)) : r.Message;
        RangeNote.Foreground = r.Ok ? new SolidColorBrush(ColorHelper.FromArgb(255, 128, 128, 128)) : Bad;
        RangeNote.Visibility = Visibility.Visible;
    }

    async void ScanClick(object sender, RoutedEventArgs e)
    {
        if (scanCts != null) { scanCts.Cancel(); return; }   // button says "Stop"
        var text = RangeBox.Text.Trim();
        var local = await Task.Run(NetProbe.Current);
        net = local;
        List<uint> targets;
        if (text.Length == 0)
        {
            if (local == null) { ScanStatus.Text = L("Not connected to a network"); return; }
            targets = NetAddress.Hosts(local.Ip, local.Prefix);
        }
        else
        {
            var r = NetAddress.ParseRange(text);
            if (!r.Ok) { RangeChanged(RangeBox, null!); return; }
            targets = Enumerable.Range(0, r.Count).Select(i => r.First + (uint)i).ToList();
        }
        var cts = scanCts = new CancellationTokenSource();
        devices.Clear(); selected = null; RebuildList();
        ScanButton.Content = L("Stop");
        ScanStatus.Text = L("Pinging…");
        try
        {
            await LanScanner.ScanAsync(local, targets, OnDevice, phase => UI(() => ScanStatus.Text = phase), cts.Token);
            if (visible) ScanStatus.Text = devices.Count == 0 ? L("Nothing found") : L("%lld online", devices.Count);
        }
        catch (OperationCanceledException) { if (visible) ScanStatus.Text = devices.Count == 0 ? L("Press Scan to look for devices") : L("%lld online", devices.Count); }
        catch (Exception ex) { if (visible) ScanStatus.Text = ex.Message; }
        finally { scanCts = null; if (visible) ScanButton.Content = L("Scan"); }
    }

    /// <summary>Called from scanner threads: queue the change, redraw at most every 300 ms.</summary>
    void OnDevice(NetDevice d) => UI(() =>
    {
        if (!devices.Contains(d)) devices.Add(d);
        if (listTimer is { IsRunning: false }) listTimer.Start();
    });

    void SearchChanged(object sender, TextChangedEventArgs e) => RebuildList();

    void RebuildList()
    {
        rebuilding = true;
        try
        {
            var q = SearchBox.Text;
            DeviceList.Items.Clear();
            HeaderRow.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Grid? keep = null;
            foreach (var d in devices.OrderBy(x => x.Ip))
            {
                if (!Devices.Matches(q, d.DisplayName, d.Vendor ?? "", d.IpText, d.MacText)) continue;
                var row = Row(d);
                DeviceList.Items.Add(row);
                if (d == selected) keep = row;
            }
            if (keep != null) DeviceList.SelectedItem = keep;
            else if (selected != null && !devices.Contains(selected)) selected = null;
        }
        finally { rebuilding = false; }
        ShowDetails();
    }

    Grid Row(NetDevice d)
    {
        var g = new Grid { ColumnSpacing = 8, Tag = d };
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(120), new GridLength(160), new GridLength(130), new GridLength(50) })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var tag = d.IsMe ? L("This PC") : d.IsRouter ? L("Router") : null;
        var cells = new[] { d.DisplayName + (tag != null ? " · " + tag : ""), d.IpText, d.VendorText, Devices.KindTitle(d.Kind), MsText(d.Ms) };
        for (int i = 0; i < cells.Length; i++)
        {
            var tb = new TextBlock { Text = cells[i], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tb, i);
            g.Children.Add(tb);
        }
        return g;
    }

    void DeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (rebuilding) return;
        selected = (DeviceList.SelectedItem as Grid)?.Tag as NetDevice;
        ShowDetails();
    }

    void ShowDetails()
    {
        DetailPanel.Children.Clear();
        if (selected is not { } d) { DetailPanel.Visibility = Visibility.Collapsed; return; }
        DetailPanel.Visibility = Visibility.Visible;
        DetailPanel.Children.Add(T(d.DisplayName, bold: true));
        DetailPanel.Children.Add(T(L("IP address %@", d.IpText)));
        if (d.Mac == null) DetailPanel.Children.Add(T(L("No hardware address or maker: this address is outside your own network, and routers don't pass those on"), 0.75));
        else
        {
            DetailPanel.Children.Add(T(L("Hardware address %@", d.MacText)));
            DetailPanel.Children.Add(T(d.PrivateMac
                ? L("Private address: a phone or laptop that hides its real address (Wi-Fi privacy). The maker can't be seen.")
                : d.Vendor == null ? L("Made by an unknown maker") : L("Made by %@", d.Vendor)));
        }
        DetailPanel.Children.Add(T(L("Looks like: %@", Devices.KindTitle(d.Kind).ToLowerInvariant())));
        DetailPanel.Children.Add(T(d.Ms == null ? L("Doesn't answer (many phones and TVs sleep or ignore pings; that's normal)")
            : d.ViaTcp ? L("Answers in %@ ms (via a TCP connection; it ignores pings)", MsText(d.Ms)) : L("Answers in %@ ms", MsText(d.Ms))));
        if (d.Ports != null)
        {
            if (d.Ports.Length == 0) DetailPanel.Children.Add(T(L("No common ports open")));
            else
            {
                DetailPanel.Children.Add(T(L("Open ports") + ": " + string.Join(", ", d.Ports.Select(p => $"{Devices.PortName(p)} {p}"))));
                foreach (var p in d.Ports)
                    if (Devices.WebUrl(d.Ip, p) is { } url)
                    {
                        var link = new HyperlinkButton { Content = L("Open in browser") + $" ({url})", Padding = new Thickness(0) };
                        link.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
                        DetailPanel.Children.Add(link);
                    }
            }
        }
        if (!d.IsMe)
        {
            var b = new Button { Content = d.Ports == null ? L("Check ports") : L("Check again") };
            b.Click += async (_, _) =>
            {
                portsCts?.Cancel();
                var cts = portsCts = new CancellationTokenSource();
                b.Content = L("Checking…"); b.IsEnabled = false;
                try { await LanScanner.CheckPorts(d, cts.Token, _ => UI(RebuildList)); }
                catch (OperationCanceledException) { }
            };
            DetailPanel.Children.Add(b);
        }
    }
}
