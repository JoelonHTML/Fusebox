using System.Globalization;
using System.Net;
using Fusebox.App.Controls;
using Fusebox.Core.Network;
using Fusebox.Core.Reports;
using Fusebox.Platform.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>Connection card (verdict, router / internet reply times, live graph, "Find the problem", details) and the Wi-Fi / cable card (§6.3).</summary>
public sealed partial class NetworkPage
{
    StackPanel graphBox = null!;
    NetGraph graph = null!;
    TextBlock graphCaption = null!;
    WifiInfo? wifi;
    double? linkMbit;
    WinNetExtras? extras;
    PingStats? router, internet;
    bool viaTcp, testing, findBusy;
    double? dnsMs;
    IPAddress? target;
    Finding? finding;
    string connSig = "";
    readonly RttRing liveRouter = new(), liveInternet = new();
    CancellationTokenSource? qualityCts, liveCts, findCts;

    // ---- lifetime ----

    async void StartPanel()
    {
        history = SpeedHistory.Parse(Prefs.Str("speedHistory"));
        RenderCards();
        await RefreshConnection(first: true);
    }

    void StopPanel()
    {
        qualityCts?.Cancel(); liveCts?.Cancel(); findCts?.Cancel();
        findBusy = false; testing = false;
    }

    static double? CableMbit(string id)
    {
        try { foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) if (nic.Id == id) return nic.Speed > 0 ? nic.Speed / 1e6 : null; }
        catch { }
        return null;
    }

    /// <summary>Reads connection + Wi-Fi (cheap, on a worker). A different address or router restarts the quality test.</summary>
    async Task RefreshConnection(bool first = false)
    {
        try
        {
            var (n, w, mbit) = await Task.Run(() => { var c = NetProbe.Current(); return (c, c?.IsWifi == true ? Wifi.Read() : null, c is { IsWifi: false } ? CableMbit(c.Id) : null); });
            if (!visible) return;
            bool moved = first || net?.Ip != n?.Ip || net?.Gateway != n?.Gateway || net?.Name != n?.Name;
            net = n; wifi = w; linkMbit = mbit;
            var sig = $"{n?.Ip}|{n?.Gateway}|{w?.Rssi}|{w?.Channel}|{w?.RxMbps}|{w?.TxMbps}|{mbit}";
            if (moved)
            {
                finding = null; router = internet = null; liveRouter.Clear(); liveInternet.Clear(); extras = null;
                RenderCards();
                StartQuality();
                if (Prefs.Bool("netConnDetails")) LoadExtras();
            }
            else if (sig != connSig) RenderCards();
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
        if (n == null) { testing = false; RenderConnection(); return; }
        testing = true; router = internet = null;
        RenderConnection();
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
        RenderConnection();
        RestartLive();
    }

    void RestartLive()
    {
        liveCts?.Cancel();
        if (!visible || net == null || testing) return;
        var cts = liveCts = new CancellationTokenSource();
        _ = LiveLoop(cts.Token);
    }

    /// <summary>One sample per second for the graph (only while the window is on screen); the connection is re-read every 5 s. Stops with the page.</summary>
    async Task LiveLoop(CancellationToken ct)
    {
        int n = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // The page stays navigated while the window is hidden in the tray or minimized: then no pings, just a cheap wait.
                bool shown = App.Main?.IsOnScreen ?? false;
                bool graphOn = shown && net != null;
                await Task.Delay(graphOn ? 1000 : 5000, ct);
                if (!visible) return;
                if (!shown) continue;
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
        if (cards.Length != 3) return;
        graphBox.Visibility = net != null ? Visibility.Visible : Visibility.Collapsed;
        if (net == null) return;
        bool inet = Prefs.Str("pingHost").Length > 0 && liveInternet.Values.Count > 0;
        var main = inet ? liveInternet.Values : liveRouter.Values;
        var second = inet ? liveRouter.Values : [];
        graph.Set(main, second, RttRing.Top(main, second));
        graphCaption.Text = inet ? L("internet — router ┄ · live") : L("router · live");
        ToolTipService.SetToolTip(graph, L("Reply times over the last two minutes (max %@ ms)", NetView.MsText(main.Concat(second).DefaultIfEmpty(0).Max())));
    }

    async void LoadExtras()
    {
        var n = net;
        var x = await Task.Run(() => WinNet.Read(n));
        if (!visible || net != n) return;
        extras = x;
        RenderConnection();
    }

    // ---- Connection card (Mac: verdict, router + internet + graph, find the problem; details: connection, quality, public IP) ----

    static string Kind(LocalNet n) => n.IsWifi ? "Wi-Fi" : "Ethernet";

    static string OkGlyph(bool? ok) => ok == false ? "" : ok == true ? "" : "";

    UIElement PingStat(NetTerm t, PingStats? s)
    {
        var sp = Stat(t, Big(s == null ? "…" : s.Got == 0 ? "–" : NetView.MsText(s.Avg), "ms"));
        if (s != null && s.Loss > 0)
        {
            var lost = Lbl(L("%lld%% lost", (int)Math.Round(s.Loss * 100)), 10, "FuseboxWarn", wrap: false);
            lost.Foreground = WarnBrush();
            sp.Children.Add(lost);
        }
        ToolTipService.SetToolTip(sp, InternetVerdict.StatsText(s, testing));
        return sp;
    }

    void RenderConnection()
    {
        if (!built || cards.Length != 3) return;
        var c = cards[0];
        if (graphBox.Parent is Panel old) old.Children.Remove(graphBox);
        c.Face.Children.Clear(); c.Details.Children.Clear();
        c.OpenKey = "netConnDetails";
        miTest.IsEnabled = !testing && net != null;
        miIp.IsEnabled = publicIp == null && ipCts == null;
        UpdateGraph();
        if (net is not { } n)
        {
            c.Face.Children.Add(Line("", L("Not connected to a network"), OkBrush(false)));
            ApplyOpen(c);
            return;
        }
        bool inetOn = Prefs.Str("pingHost").Length > 0;
        int? rssi = n.IsWifi && wifi is { Connected: true } ? wifi.Rssi : null;

        if (testing) c.Face.Children.Add(Line("", L("Testing the connection…")));
        else if (finding == null && !findBusy && (router != null || internet != null))
        {
            // the thorough check below replaces the quick verdict: never two contradicting lines
            var v = InternetVerdict.Verdict(!n.IsWifi, rssi, null, router, internet, dnsMs, inetOn);
            var line = Line(OkGlyph(v.Ok), v.Ok == true ? L("All good") : v.Text, OkBrush(v.Ok));
            ToolTipService.SetToolTip(line, v.Text);
            c.Face.Children.Add(line);
        }

        var stats = new Grid { ColumnSpacing = 18 };
        void Add(UIElement e, GridLength w) { stats.ColumnDefinitions.Add(new ColumnDefinition { Width = w }); Grid.SetColumn((FrameworkElement)e, stats.ColumnDefinitions.Count - 1); stats.Children.Add(e); }
        if (n.Gateway != null) Add(PingStat(NetTerm.Router, router), GridLength.Auto);
        if (inetOn) Add(PingStat(NetTerm.Internet, internet), GridLength.Auto);
        Add(graphBox, new GridLength(1, GridUnitType.Star));
        c.Face.Children.Add(stats);

        // "Is it my Wi-Fi or the internet?": button, then one verdict with the numbers under it. Save report… stays next to it after every measurement.
        if (findBusy)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
            row.Children.Add(Lbl(L("Checking Wi-Fi, router, DNS and internet… (about 6 s)"), 10, "FuseboxOnRowSecondary"));
            c.Face.Children.Add(row);
        }
        else
        {
            if (finding is { } f)
            {
                c.Face.Children.Add(Line(OkGlyph(f.Ok), f.Text, OkBrush(f.Ok)));
                if (f.Numbers.Length > 0) { var nt = Lbl(f.Numbers, 10); nt.Margin = new Thickness(22, 0, 0, 0); nt.IsTextSelectionEnabled = true; c.Face.Children.Add(nt); }
            }
            var pill = Pill("", finding == null ? L("Find the problem") : L("Check again"),
                L("Is it your Wi-Fi or the internet? About 6 s: pings your router and 1.1.1.1, times fresh DNS lookups and reads the Wi-Fi signal"), (_, _) => FindProblem());
            var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
            pills.Children.Add(pill); pills.Children.Add(SaveReportPill());
            c.Face.Children.Add(pills);
        }

        // details
        var d = c.Details;
        d.Children.Add(Kv(NetTerm.Type, $"{Kind(n)} ({n.Name})"));
        if (extras is { } x)
        {
            if (WinExtras.ProfileWord(x.ProfileCategory) is { } pw) d.Children.Add(Kv(NetTerm.Profile, pw));
            d.Children.Add(Kv(NetTerm.Vpn, x.Vpn ? L("VPN on") : L("Off")));
            if (x.EncryptedDns is { } doh) d.Children.Add(Kv(NetTerm.EncryptedDns, WinExtras.OnOff(doh)));
            d.Children.Add(Kv(NetTerm.Metered, WinExtras.Metered(x.Metered)));
        }
        d.Children.Add(Kv(NetTerm.Ipv4, NetAddress.IpString(n.Ip)));
        var v6 = n.Ipv6Global;
        d.Children.Add(Kv(NetTerm.Ipv6, v6.Length > 0 ? v6[0] + (v6.Length > 1 ? $" (+{v6.Length - 1})" : "") : L("none")));
        d.Children.Add(Kv(NetTerm.Gateway, n.Gateway is { } g ? NetAddress.IpString(g) : L("unknown"), L("Router")));
        d.Children.Add(Kv(NetTerm.DnsServers, n.Dns.Length == 0 ? L("unknown") : string.Join(", ", n.Dns.Take(3))));
        d.Children.Add(Kv(NetTerm.Ipv6Internet, WinExtras.YesNo(v6.Length > 0 && n.Ipv6Router)));

        d.Children.Add(Subhead(L("Quality (10 pings)")));
        d.Children.Add(Kv(NetTerm.Router, InternetVerdict.StatsText(router, testing)));
        if (inetOn)
        {
            d.Children.Add(Kv(NetTerm.Internet, InternetVerdict.StatsText(internet, testing) + (viaTcp ? " (TCP)" : "")));
            d.Children.Add(Kv(NetTerm.DnsLookup, dnsMs is { } ms ? NetView.MsText(ms) + " ms" : "–"));
        }
        else d.Children.Add(Kv(NetTerm.Internet, L("off (Settings)")));

        // public IP: only on a click, never stored
        if (publicIp is { } ip)
        {
            d.Children.Add(Subhead(L("Public IP")));
            d.Children.Add(Kv(NetTerm.PublicIp, ip.Ip));
            if (ip.Isp != null) d.Children.Add(Kv(NetTerm.PublicIp, ip.Isp, L("Provider")));
            if (ip.Place != null) d.Children.Add(Kv(NetTerm.PublicIp, ip.Place, L("Location")));
        }
        else if (publicIpState != null) d.Children.Add(Line("", publicIpState));
        else
        {
            var b = Pill("", L("Show public IP"), L("Asks speed.cloudflare.com which address and provider you appear as"), (_, _) => LoadPublicIp());
            b.HorizontalAlignment = HorizontalAlignment.Left; b.Margin = new Thickness(0, 4, 0, 0);
            d.Children.Add(b);
        }
        ApplyOpen(c);
    }

    // ---- Wi-Fi card, or the cable / not-connected variant (Mac "link") ----

    void RenderLink()
    {
        if (!built || cards.Length != 3) return;
        var c = cards[1];
        c.Face.Children.Clear(); c.Details.Children.Clear();
        c.OpenKey = null;
        if (net is { IsWifi: true } && wifi is { Connected: true, Rssi: { } rssi } w)
        {
            // Never the Wi-Fi name or BSSID. Windows gives no noise value, so no noise / SNR.
            c.OpenKey = "netLinkDetails"; c.Terms = NetTerms.Wifi; c.Icon.Glyph = ""; c.Title.Text = "Wi-Fi";
            bool? ok = rssi >= -60 ? true : rssi >= -70 ? null : false;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };

            var sig = new StackPanel { Spacing = 1 };
            sig.Children.Add(Term(NetTerm.Signal));
            var big = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            big.Children.Add(Big(rssi.ToString(CultureInfo.InvariantCulture), "dBm"));
            var bars = SignalBars(rssi, OkBrush(ok)); bars.Margin = new Thickness(0, 0, 0, 5);
            big.Children.Add(bars);
            sig.Children.Add(big);
            var word = Lbl(WifiMath.Word(rssi), 10, "FuseboxOnRowSecondary", wrap: false); word.Foreground = OkBrush(ok);
            sig.Children.Add(word);
            Tip(sig, NetTerm.Signal);
            head.Children.Add(sig);

            var rate = w.TxMbps ?? w.RxMbps;
            if (rate is > 0) head.Children.Add(Stat(NetTerm.LinkSpeed, Big(rate.Value.ToString(CultureInfo.InvariantCulture), "Mbit/s")));
            c.Face.Children.Add(head);
            var facts = new[] { w.Band, w.Phy?.Split(" (")[0] }.OfType<string>().ToList();
            if (facts.Count > 0) c.Face.Children.Add(Lbl(string.Join(" · ", facts), 11, "FuseboxOnRowSecondary"));

            c.Details.Children.Add(Kv(NetTerm.Signal, $"{rssi} dBm ({WifiMath.Word(rssi)})"));
            if (rate is > 0) c.Details.Children.Add(Kv(NetTerm.LinkSpeed, $"{rate} Mbit/s"));
            if (w.Channel is { } ch) c.Details.Children.Add(Kv(NetTerm.Channel, string.Join(" · ", new[] { ch.ToString(CultureInfo.InvariantCulture), w.Band, w.WidthMhz is { } wd ? wd + " MHz" : null }.OfType<string>())));
            if (w.Phy != null) c.Details.Children.Add(Kv(NetTerm.Standard, w.Phy));
        }
        else if (net is { IsWifi: true })
        {
            c.Terms = NetTerms.Wifi; c.Icon.Glyph = ""; c.Title.Text = "Wi-Fi";
            c.Face.Children.Add(Line("", L("No Wi-Fi signal information available")));
        }
        else if (net is { } n)
        {
            c.Terms = NetTerms.Wired; c.Icon.Glyph = ""; c.Title.Text = L("Cable");
            c.Face.Children.Add(Line("", L("Connected by cable, not Wi-Fi")));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            if (linkMbit is { } mb)
            {
                var text = LinkSpeedText(mb); var i = text.IndexOf(' ');
                row.Children.Add(Stat(NetTerm.LinkSpeed, Big(text[..i], text[(i + 1)..])));
            }
            var ad = new StackPanel { Spacing = 1, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            ad.Children.Add(Term(NetTerm.Cable, L("Adapter")));
            var nm = Lbl(n.Description.Length > 0 ? n.Description : n.Name, 13, "FuseboxOnRow"); ad.Children.Add(nm);
            ad.Children.Add(Lbl(n.Name, 10, "FuseboxOnRowSecondary"));
            Tip(ad, NetTerm.Cable);
            row.Children.Add(ad);
            c.Face.Children.Add(row);
        }
        else
        {
            c.Terms = NetTerms.Wired; c.Icon.Glyph = ""; c.Title.Text = "Wi-Fi";
            c.Face.Children.Add(Line("", L("Not connected"), OkBrush(false)));
        }
        ApplyOpen(c);
    }

    /// <summary>Four bars like the tray icon: the stronger the signal, the more are filled.</summary>
    static FrameworkElement SignalBars(int rssi, Microsoft.UI.Xaml.Media.Brush color)
    {
        int level = rssi >= -55 ? 4 : rssi >= -62 ? 3 : rssi >= -70 ? 2 : rssi >= -78 ? 1 : 0;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Bottom };
        for (int i = 0; i < 4; i++)
            row.Children.Add(new Border { Width = 4, Height = 6 + i * 4, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Bottom,
                Background = i < level ? color : B("FuseboxOnRowSecondary"), Opacity = i < level ? 1 : 0.25 });
        AutomationHelp(row, $"{level}/4");
        return row;
    }

    static void AutomationHelp(FrameworkElement e, string text) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(e, text);

    // ---- report and "find the problem" ----

    /// <summary>Shown after every measurement (Find the problem, speed test), so the result can be kept or sent at once.</summary>
    Button SaveReportPill() => Pill("", L("Save report…"), L("A PDF with signal, ping and speed numbers and graphs to send to your provider or helpdesk. No Wi-Fi name or addresses."), (_, _) => ShareReport());

    /// <summary>"Save report…": builds the text from what is already measured (nothing new runs), scrubs it, shows the share sheet (Copy, Save as PDF…).</summary>
    async void ShareReport()
    {
        var n = net; var w = wifi is { Connected: true, Rssi: not null } ? wifi : null; var x = extras;
        var inet = Prefs.Str("pingHost").Length > 0 ? internet : null;
        var wide = n?.IsWifi == true ? w : null;
        var v6 = n == null ? (bool?)null : n.Ipv6Global.Length > 0 && n.Ipv6Router;
        var input = new NetReportInput(DateTime.Now, n == null ? null : Kind(n), x?.Vpn, x?.ProfileCategory, x?.EncryptedDns, x?.Metered,
            wide?.Rssi, (wide?.TxMbps ?? wide?.RxMbps) is > 0 and var rate ? rate : null, wide?.Channel, wide?.Band, wide?.WidthMhz, wide?.Phy,
            router, inet, dnsMs, v6, finding, history.FirstOrDefault(), all.Count > 0 ? all.Count : null,
            history.ToArray(), liveRouter.Values.ToArray(), inet != null ? liveInternet.Values.ToArray() : null);   // copies: the rings keep moving
        var pc = CheckupShare.PcLine();
        var text = await Task.Run(() => NetReport.Build(input, Fusebox.Platform.Checkup.CheckupReader.Identity()));
        if (!visible) return;
        await CheckupShare.ShowSheetAsync(XamlRoot, L("Network report"), L("Contains no Wi-Fi name, network addresses, computer name or user name."), text,
            "Fusebox-network-" + input.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            () => NetPdf.Build(input with { Pc = pc() }, Fusebox.Platform.Checkup.CheckupReader.Identity()));
    }

    async void FindProblem()
    {
        if (findBusy) return;
        findCts?.Cancel();
        var cts = findCts = new CancellationTokenSource();
        finding = null; findBusy = true;
        RenderConnection();
        try
        {
            var (f, _, _) = await Task.Run(() => Troubleshooter.RunAsync(cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !visible) return;
            finding = f;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { finding = new Finding(FindCode.NotConnected, ex.Message, false, ""); }
        finally { if (findCts == cts) { findBusy = false; if (visible) RenderConnection(); } }
    }
}
