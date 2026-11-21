using System.Globalization;
using System.Net;
using Fusebox.App.Controls;
using Fusebox.Core.Network;
using Fusebox.Platform.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>Internet panel (§6.3): summary lines, live graph, "Find the problem", expandable details (connection, Wi-Fi, quality, speed test).</summary>
public sealed partial class NetworkPage
{
    StackPanel summary = null!, graphBox = null!;
    ScrollViewer details = null!;
    NetColumns detailsGrid = null!;
    NetGraph graph = null!;
    TextBlock graphCaption = null!;
    FontIcon chevronIcon = null!;
    Button chevron = null!;
    bool panelOpen;
    WifiInfo? wifi;
    WinNetExtras? extras;
    PingStats? router, internet;
    bool viaTcp, testing, findBusy;
    double? dnsMs;
    IPAddress? target;
    Finding? finding;
    string connSig = "";
    readonly RttRing liveRouter = new(), liveInternet = new();
    CancellationTokenSource? qualityCts, liveCts, findCts;

    void BuildPanel()
    {
        var root = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        var top = new Grid { ColumnSpacing = 10 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary = new StackPanel { Spacing = 5 };

        graph = new NetGraph { Width = 170, Height = 46 };
        graphCaption = Lbl("", 9, "FuseboxOnRowSecondary", wrap: false);
        graphCaption.HorizontalAlignment = HorizontalAlignment.Right;
        graphBox = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };
        graphBox.Children.Add(graph); graphBox.Children.Add(graphCaption);
        Grid.SetColumn(graphBox, 1);

        chevronIcon = new FontIcon { Glyph = "", FontSize = 11, Foreground = B("FuseboxOnRow"), RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = new RotateTransform() };
        chevron = Plain(chevronIcon, null, 20, 20, (_, _) => SetOpen(!panelOpen));
        chevron.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(chevron, 2);
        top.Children.Add(summary); top.Children.Add(graphBox); top.Children.Add(chevron);

        detailsGrid = new NetColumns { Margin = new Thickness(0, 2, 0, 0) };
        details = new ScrollViewer { Content = detailsGrid, Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Auto };
        root.Children.Add(top); root.Children.Add(details);
        InternetCard.Child = root;
    }

    // ---- lifetime ----

    async void StartPanel()
    {
        history = SpeedHistory.Parse(Prefs.Str("speedHistory"));
        panelOpen = Prefs.Bool("netPanelOpen");
        ApplyOpen();
        await RefreshConnection(first: true);
    }

    void StopPanel()
    {
        qualityCts?.Cancel(); liveCts?.Cancel(); findCts?.Cancel();
        findBusy = false; testing = false;
    }

    /// <summary>Reads connection + Wi-Fi (cheap, on a worker). A different address or router restarts the quality test.</summary>
    async Task RefreshConnection(bool first = false)
    {
        try
        {
            var (n, w) = await Task.Run(() => { var c = NetProbe.Current(); return (c, c?.IsWifi == true ? Wifi.Read() : null); });
            if (!visible) return;
            bool moved = first || net?.Ip != n?.Ip || net?.Gateway != n?.Gateway || net?.Name != n?.Name;
            net = n; wifi = w;
            var sig = $"{n?.Ip}|{n?.Gateway}|{w?.Rssi}|{w?.Channel}|{w?.RxMbps}|{w?.TxMbps}";
            if (moved)
            {
                finding = null; router = internet = null; liveRouter.Clear(); liveInternet.Clear(); extras = null;
                RenderSummary(); RenderDetails();
                StartQuality();
                if (panelOpen) LoadExtras();
            }
            else if (sig != connSig) { RenderSummary(); RenderDetails(); }
            connSig = sig;
        }
        catch { }
    }

    /// <summary>10 pings to the router and the ping host (or TCP 443), plus the DNS lookup time. Then the live graph runs.</summary>
    async void StartQuality()
    {
        qualityCts?.Cancel();
        var cts = qualityCts = new CancellationTokenSource();
        var n = net;
        if (n == null) { testing = false; RenderSummary(); return; }
        testing = true; router = internet = null;
        RenderSummary(); RenderDetails();
        try
        {
            var host = Prefs.Str("pingHost");
            var q = await Task.Run(() => InternetPing.QualityAsync(n, host, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !visible) return;
            router = q.Router; internet = q.Internet; viaTcp = q.ViaTcp; dnsMs = q.DnsMs; target = q.Target;
        }
        catch (OperationCanceledException) { return; }
        catch { }
        if (qualityCts != cts) return;
        testing = false;
        RenderSummary(); RenderDetails();
        RestartLive();
    }

    void RestartLive()
    {
        liveCts?.Cancel();
        if (!visible || net == null || testing) return;
        var cts = liveCts = new CancellationTokenSource();
        _ = LiveLoop(cts.Token);
    }

    /// <summary>One sample per second for the graph (only while the graph is shown); the connection is re-read every 5 s. Stops with the page.</summary>
    async Task LiveLoop(CancellationToken ct)
    {
        int n = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool graphOn = band >= NetBand.Kind && net != null;
                await Task.Delay(graphOn ? 1000 : 5000, ct);
                if (!visible) return;
                if (graphOn && net is { } cur)
                {
                    var tgt = target; bool tcp = viaTcp;
                    var (r, i) = await Task.Run(() => InternetPing.Tick(cur, tgt, tcp, ct), ct);
                    if (ct.IsCancellationRequested || !visible) return;
                    if (cur.Gateway != null) liveRouter.Push(r);
                    if (tgt != null) liveInternet.Push(i);
                    UpdateGraph();
                }
                if (++n % (graphOn ? 5 : 1) == 0) await RefreshConnection();
            }
        }
        catch (OperationCanceledException) { }
    }

    void UpdateGraph()
    {
        bool show = band >= NetBand.Kind && net != null;
        graphBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        bool inet = Prefs.Str("pingHost").Length > 0 && liveInternet.Values.Count > 0;
        var main = inet ? liveInternet.Values : liveRouter.Values;
        var second = inet ? liveRouter.Values : [];
        graph.Set(main, second, RttRing.Top(main, second));
        graphCaption.Text = inet ? L("internet — router ┄ · live") : L("router · live");
        ToolTipService.SetToolTip(graph, L("Reply times over the last two minutes (max %@ ms)", NetView.MsText(main.Concat(second).DefaultIfEmpty(0).Max())));
    }

    // ---- expand / collapse (netPanelOpen) ----

    void SetOpen(bool open)
    {
        panelOpen = open;
        Prefs.Set("netPanelOpen", open);
        ApplyOpen();
        if (open && extras == null) LoadExtras();
    }

    void ApplyOpen()
    {
        details.Visibility = panelOpen ? Visibility.Visible : Visibility.Collapsed;
        ((RotateTransform)chevronIcon.RenderTransform).Angle = panelOpen ? 180 : 0;
        ToolTipService.SetToolTip(chevron, panelOpen ? L("Fewer details") : L("More details: connection, Wi-Fi, quality, speed test"));
        details.MaxHeight = Math.Max(140, Root.ActualHeight - 230);
        if (panelOpen) RenderDetails();
    }

    async void LoadExtras()
    {
        var n = net;
        var x = await Task.Run(() => WinNet.Read(n));
        if (!visible || net != n) return;
        extras = x;
        RenderDetails();
    }

    // ---- summary lines (§6.3.1) ----

    static string Kind(LocalNet n) => n.IsWifi ? "Wi-Fi" : "Ethernet";

    void RenderSummary()
    {
        if (!built) return;
        summary.Children.Clear();
        UpdateGraph();
        if (net is not { } n)
        {
            summary.Children.Add(Line("", L("Not connected to a network")));
            return;
        }
        int? rssi = n.IsWifi && wifi is { Connected: true } ? wifi.Rssi : null;
        summary.Children.Add(Line(n.IsWifi ? "" : "", InternetVerdict.ConnectionLine(NetAddress.IpString(n.Ip), Kind(n), rssi)));

        var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        if (n.Gateway != null) stats.Children.Add(Stat(L("Router"), router));
        if (Prefs.Str("pingHost").Length > 0) stats.Children.Add(Stat("Internet", internet));
        if (stats.Children.Count > 0) summary.Children.Add(stats);

        if (testing) summary.Children.Add(Line("", L("Testing the connection…")));
        else if (finding == null && !findBusy && (router != null || internet != null))
        {
            var v = InternetVerdict.Verdict(!n.IsWifi, rssi, null, router, internet, dnsMs, Prefs.Str("pingHost").Length > 0);
            summary.Children.Add(Line(OkGlyph(v.Ok), v.Text, OkBrush(v.Ok)));
        }

        // "Find the problem" (§6.3.4)
        if (findBusy)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
            row.Children.Add(Lbl(L("Checking Wi-Fi, router, DNS and internet… (about 6 s)"), 11, "FuseboxOnRowSecondary"));
            summary.Children.Add(row);
        }
        else
        {
            if (finding is { } f)
            {
                summary.Children.Add(Line(OkGlyph(f.Ok), f.Text, OkBrush(f.Ok)));
                if (f.Numbers.Length > 0) { var nt = Lbl(f.Numbers, 10); nt.Margin = new Thickness(22, 0, 0, 0); summary.Children.Add(nt); }
            }
            var pill = Pill("", finding == null ? L("Find the problem") : L("Check again"),
                L("Is it your Wi-Fi or the internet? About 6 s: pings your router and 1.1.1.1, times fresh DNS lookups and reads the Wi-Fi signal"), (_, _) => FindProblem());
            pill.HorizontalAlignment = HorizontalAlignment.Left;
            summary.Children.Add(pill);
        }
    }

    static string OkGlyph(bool? ok) => ok == false ? "" : ok == true ? "" : "";

    UIElement Stat(string title, PingStats? s)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var t = Lbl(title, 11, "FuseboxOnRow", wrap: false);
        var v = Num(s == null ? "…" : NetView.MsText(s.Avg), 15);
        var ms = Lbl("ms", 11, "FuseboxOnRowSecondary", wrap: false);
        foreach (var x in new FrameworkElement[] { t, v, ms }) x.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(t); row.Children.Add(v); row.Children.Add(ms);
        if (s != null && s.Loss > 0)
        {
            var lost = Lbl(L("%lld%% lost", (int)Math.Round(s.Loss * 100)), 11, "FuseboxWarn", wrap: false);
            lost.Foreground = WarnBrush(); lost.VerticalAlignment = VerticalAlignment.Bottom;
            row.Children.Add(lost);
        }
        return row;
    }

    async void FindProblem()
    {
        if (findBusy) return;
        findCts?.Cancel();
        var cts = findCts = new CancellationTokenSource();
        finding = null; findBusy = true;
        RenderSummary();
        try
        {
            var (f, _, _) = await Task.Run(() => Troubleshooter.RunAsync(cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !visible) return;
            finding = f;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { finding = new Finding(FindCode.NotConnected, ex.Message, false, ""); }
        finally { if (findCts == cts) { findBusy = false; if (visible) RenderSummary(); } }
    }

    // ---- details (§6.3.5) ----

    StackPanel Section(string title)
    {
        var s = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
        s.Children.Add(Lbl(title, 11, "FuseboxOnRow"));
        return s;
    }

    static Grid Kv(string key, string value)
    {
        var g = new Grid { ColumnSpacing = 6 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = Lbl(key, 11, "FuseboxOnRowSecondary");
        var v = Lbl(value, 11, "FuseboxOnRow");
        v.IsTextSelectionEnabled = true;
        Grid.SetColumn(v, 1);
        g.Children.Add(k); g.Children.Add(v);
        return g;
    }

    void RenderDetails()
    {
        if (!built || !panelOpen) return;
        detailsGrid.Children.Clear();
        if (net is { } n)
        {
            var c = Section(L("Connection"));
            c.Children.Add(Kv(L("Type"), $"{Kind(n)} ({n.Name})"));
            c.Children.Add(Kv("IPv4", NetAddress.IpString(n.Ip)));
            var v6 = n.Ipv6Global;
            c.Children.Add(Kv("IPv6", v6.Length > 0 ? v6[0] + (v6.Length > 1 ? $" (+{v6.Length - 1})" : "") : L("none")));
            c.Children.Add(Kv(L("Router"), n.Gateway is { } g ? NetAddress.IpString(g) : L("unknown")));
            c.Children.Add(Kv("DNS", n.Dns.Length == 0 ? L("unknown") : string.Join(", ", n.Dns.Take(3))));
            c.Children.Add(Kv(L("IPv6 internet"), WinExtras.YesNo(v6.Length > 0 && n.Ipv6Router)));
            if (extras is { } x)
            {
                if (WinExtras.ProfileWord(x.ProfileCategory) is { } pw) c.Children.Add(Kv(L("Network"), pw));
                c.Children.Add(Kv("VPN", WinExtras.YesNo(x.Vpn)));
                if (x.EncryptedDns is { } doh) c.Children.Add(Kv(L("Encrypted DNS"), WinExtras.OnOff(doh)));
                c.Children.Add(Kv(L("Metered"), WinExtras.Metered(x.Metered)));
            }
            detailsGrid.Children.Add(c);
        }
        if (net is { IsWifi: true } && wifi is { Connected: true, Rssi: { } rssi } w)
        {
            // Never the Wi-Fi name or BSSID. Windows gives no noise value, so no Noise/SNR row.
            var s = Section("Wi-Fi");
            s.Children.Add(Kv(L("Signal"), $"{rssi} dBm ({WifiMath.Word(rssi)})"));
            if ((w.TxMbps ?? w.RxMbps) is { } rate && rate > 0) s.Children.Add(Kv(L("Link speed"), $"{rate} Mbit/s"));
            if (w.Channel is { } ch) s.Children.Add(Kv(L("Channel"), string.Join(" · ", new[] { ch.ToString(CultureInfo.InvariantCulture), w.Band, w.WidthMhz is { } wd ? wd + " MHz" : null }.OfType<string>())));
            if (w.Phy != null) s.Children.Add(Kv(L("Standard"), w.Phy));
            detailsGrid.Children.Add(s);
        }
        var q = Section(L("Quality (10 pings)"));
        bool inetOn = Prefs.Str("pingHost").Length > 0;
        q.Children.Add(Kv(L("Router"), InternetVerdict.StatsText(router, testing)));
        if (inetOn)
        {
            q.Children.Add(Kv("Internet", InternetVerdict.StatsText(internet, testing) + (viaTcp ? " (TCP)" : "")));
            q.Children.Add(Kv(L("DNS lookup"), dnsMs is { } d ? NetView.MsText(d) + " ms" : "–"));
        }
        else q.Children.Add(Kv("Internet", L("off (Settings)")));
        var again = Pill("", testing ? L("Testing…") : L("Test again"), null, (_, _) => StartQuality());
        again.IsEnabled = !testing; again.HorizontalAlignment = HorizontalAlignment.Left; again.Margin = new Thickness(0, 2, 0, 0);
        q.Children.Add(again);
        detailsGrid.Children.Add(q);

        var sp = Section(L("Speed test"));
        speedBox = new StackPanel { Spacing = 3 };
        sp.Children.Add(speedBox);
        detailsGrid.Children.Add(sp);
        RenderSpeed();
    }
}
