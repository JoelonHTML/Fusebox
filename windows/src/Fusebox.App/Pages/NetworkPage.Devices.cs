using Fusebox.App.Controls;
using Fusebox.Core.Look;
using Fusebox.Core.Network;
using Fusebox.Platform.Network;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>Scan (§6.10), remembered devices (§6.11) and the device rows (§6.9).</summary>
public sealed partial class NetworkPage
{
    readonly Dictionary<string, RowView> rows = [];
    string? openId;
    CancellationTokenSource? portsCts;

    // ---- scan ----

    /// <summary>Fresh scan of this network or the custom range. Starting one while another runs cancels the old one (it keeps its own state object).</summary>
    async void StartScan()
    {
        scanCts?.Cancel();
        var cts = scanCts = new CancellationTokenSource();
        var state = scan = new ScanState();
        scanning = true;
        var n = await Task.Run(NetProbe.Current);
        if (!visible || cts.IsCancellationRequested) { if (scanCts == cts) { scanCts = null; scanning = false; } return; }
        if (net == null) net = n;
        var info = LanScanner.Prepare(state, n, customHosts, out var hosts);
        int remote = info == null ? 0 : hosts.Count(h => !info.IsLocal(h));
        scanStart = DateTime.UtcNow; expected = NetView.ExpectedSeconds(customHosts != null, hosts.Count, remote);
        scanBar.Value = 0; phaseText.Text = L("Pinging…");
        progressTimer?.Start();
        RenderToolbar(); RenderList();
        try
        {
            if (info != null && n != null)
            {
                await Task.Run(() => LanScanner.ScanAsync(state, n, hosts, () => UI(ScheduleRender), ph => UI(() => { if (scanCts == cts) phaseText.Text = ph; }), cts.Token), cts.Token);
                if (!cts.IsCancellationRequested && visible) Remember(info);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (state.Gate) state.Scanning = false;
            if (scanCts == cts)
            {
                scanCts = null; scanning = false; progressTimer?.Stop();
                if (visible) { RenderToolbar(); RenderList(); }
            }
        }
    }

    void ScheduleRender() { if (listTimer is { IsRunning: false }) listTimer.Start(); }

    List<NetDevice> BuildDevices() => NetDevices.Build(scan, store, mac => Devices.TableReady ? Devices.Vendor(mac) : null, DateTime.UtcNow);

    /// <summary>After a completed scan: update the remembered devices and, if asked, notify about new ones (max 3 per scan).</summary>
    void Remember(ScanInfo info)
    {
        var now = DateTime.UtcNow;
        var online = BuildDevices().Where(d => d.Online).ToList();
        var fresh = store.Remember(online, info.NetworkKey, now);
        if (Prefs.Bool("notifyNewDevice") && store.Created < now.AddSeconds(-60))
            foreach (var d in fresh.Take(3))
                Notify.Send(L("New device on your network"), $"{d.Name} · {d.IpText}" + (d.HasVendor ? " · " + d.Vendor : ""));
    }

    // ---- list ----

    void RenderList(bool force = false)
    {
        if (!built) return;
        all = BuildDevices();
        bool macShown = NetView.Shows(band, hide, NetCol.Mac);
        var shown = NetView.Sort(all.Where(d => NetView.Matches(filter, d) && d.Matches(search, macShown)), sortCol, ascending);
        var ids = all.Select(d => d.Id).ToHashSet();
        foreach (var gone in rows.Keys.Where(k => !ids.Contains(k)).ToList()) rows.Remove(gone);
        bool tight = ThemeService.Current.RowStyle is RowStyle.Lines or RowStyle.Plain;
        DeviceList.Spacing = tight ? 0 : 6;

        var list = new List<UIElement>(shown.Count);
        for (int i = 0; i < shown.Count; i++)
        {
            var d = shown[i];
            if (!rows.TryGetValue(d.Id, out var rv)) rows[d.Id] = rv = new RowView(this);
            rv.Update(d, i + 1, openId == d.Id, band, hide, force);
            list.Add(rv.Box);
        }
        if (!DeviceList.Children.SequenceEqual(list))
        {
            DeviceList.Children.Clear();
            foreach (var u in list) DeviceList.Children.Add(u);
        }
        var empty = shown.Count == 0 ? NetView.EmptyText(scan.Info != null || scanning, Prefs.Bool("netAutoScan"), search, scanning, filter, customRange) : null;
        EmptyText.Visibility = empty == null ? Visibility.Collapsed : Visibility.Visible;
        if (empty != null) EmptyText.Text = empty;
        bool blocked; lock (scan.Gate) blocked = scan.Blocked;
        BlockedHint.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        if (built) countText.Text = NetView.CountText(all);
    }

    void Toggle(string id) { openId = openId == id ? null : id; RenderList(); }

    // ---- actions ----

    async void CheckPorts(NetDevice d)
    {
        portsCts?.Cancel();
        var cts = portsCts = new CancellationTokenSource();
        var state = scan;
        RenderList();
        var t = LanScanner.CheckPorts(state, d.Ip, cts.Token);
        RenderList();   // shows "Checking…"
        try { await t; } catch (OperationCanceledException) { return; } catch { }
        if (visible && scan == state) RenderList();
    }

    async Task<bool> Wake(NetDevice d)
    {
        if (d.Mac == null || scan.Info is not { } info) return false;
        try { return await NetSockets.Wake(d.Mac, info.Ip | ~info.Mask); } catch { return false; }
    }

    async void Rename(NetDevice d)
    {
        if (d.Mac == null) return;
        var box = new TextBox { Header = L("Name"), Text = d.Custom ?? d.Name, MaxLength = 64 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = L("Leave empty to use the name Fusebox finds itself."), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        var dlg = new ContentDialog
        {
            Title = L("Rename device"), Content = panel, PrimaryButtonText = L("Save"), CloseButtonText = L("Cancel"),
            DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary || !visible) return;
        store.Rename(d.MacText, box.Text, DateTime.UtcNow);
        RenderList();
    }

    void Forget(NetDevice d)
    {
        if (d.Mac == null) return;
        store.Forget(d.MacText);
        if (openId == d.Id) openId = null;
        RenderList();
    }

    // ---- one row ----

    sealed class RowView
    {
        readonly NetworkPage p;
        public readonly Border Box = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly StackPanel stack = new();
        readonly Grid summary = new() { ColumnSpacing = 8, Background = new SolidColorBrush(Colors.Transparent) };
        readonly FontIcon icon = new() { FontSize = 12, Width = 18, HorizontalAlignment = HorizontalAlignment.Center };
        readonly Grid nameCell = new() { ColumnSpacing = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly TextBlock nameTb, tagTb, newTb, lastTb, ipTb, macTb, vendorTb, kindTb, msTb;
        readonly Border newBadge = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel nameBadges = new() { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        StackPanel? detailsPanel;
        public NetDevice Dev = null!;
        bool open;
        NetBand lastBand = (NetBand)(-1);
        string lastHide = "\0";
        string detailSig = "";

        public RowView(NetworkPage page)
        {
            p = page;
            nameTb = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = B("FuseboxOnRow") };
            Look.Label(nameTb, 12);
            tagTb = Lbl("", 10, "FuseboxOnRowSecondary", wrap: false); tagTb.VerticalAlignment = VerticalAlignment.Center;
            newTb = new TextBlock { Text = L("New") }; Look.Label(newTb, 9);
            newBadge.Child = newTb;
            lastTb = Lbl("", 10, "FuseboxOnRowSecondary", wrap: false); lastTb.VerticalAlignment = VerticalAlignment.Center;
            nameBadges.Children.Add(tagTb); nameBadges.Children.Add(newBadge); nameBadges.Children.Add(lastTb);
            nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(nameBadges, 1);
            nameCell.Children.Add(nameTb); nameCell.Children.Add(nameBadges);
            // the name gives way to the badges, with an ellipsis
            nameCell.SizeChanged += (_, _) => FitName();
            nameBadges.SizeChanged += (_, _) => FitName();

            ipTb = Num("", 12); ipTb.VerticalAlignment = VerticalAlignment.Center;
            macTb = new TextBlock { Foreground = B("FuseboxOnRow"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
            vendorTb = Lbl("", 11, "FuseboxOnRow", wrap: false); vendorTb.TextTrimming = TextTrimming.CharacterEllipsis; vendorTb.VerticalAlignment = VerticalAlignment.Center;
            kindTb = Lbl("", 11, "FuseboxOnRow", wrap: false); kindTb.TextTrimming = TextTrimming.CharacterEllipsis; kindTb.VerticalAlignment = VerticalAlignment.Center;
            msTb = Num("", 12); msTb.HorizontalAlignment = HorizontalAlignment.Right; msTb.VerticalAlignment = VerticalAlignment.Center;
            icon.Foreground = B("FuseboxOnRowSecondary"); icon.VerticalAlignment = VerticalAlignment.Center;

            nameCell.VerticalAlignment = VerticalAlignment.Center;
            summary.Children.Add(icon); summary.Children.Add(nameCell); summary.Children.Add(ipTb); summary.Children.Add(macTb);
            summary.Children.Add(vendorTb); summary.Children.Add(kindTb); summary.Children.Add(msTb);
            summary.Tapped += (_, _) => p.Toggle(Dev.Id);
            stack.Children.Add(summary);
            Box.Child = stack;
        }

        void FitName() => nameTb.MaxWidth = Math.Max(24, nameCell.ActualWidth - nameBadges.ActualWidth - (nameBadges.ActualWidth > 0 ? 5 : 0));

        /// <summary>Column set for the band and the hidden list (netHide): only visible columns exist, so spacing stays right.</summary>
        public void Layout(NetBand band, string hide)
        {
            summary.ColumnDefinitions.Clear();
            int col = 0;
            void Add(UIElement e, GridLength w, bool show)
            {
                e.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show) return;
                summary.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                Grid.SetColumn((FrameworkElement)e, col++);
            }
            Add(icon, new GridLength(18), true);
            Add(nameCell, new GridLength(1, GridUnitType.Star), true);
            Add(ipTb, new GridLength(NetView.Width(NetCol.Ip)), true);
            Add(macTb, new GridLength(NetView.Width(NetCol.Mac)), NetView.Shows(band, hide, NetCol.Mac));
            Add(vendorTb, new GridLength(NetView.Width(NetCol.Vendor)), NetView.Shows(band, hide, NetCol.Vendor));
            Add(kindTb, new GridLength(NetView.Width(NetCol.Kind)), NetView.Shows(band, hide, NetCol.Kind));
            Add(msTb, new GridLength(NetView.Width(NetCol.Ms)), true);
            lastBand = band; lastHide = hide;
        }

        public void Update(NetDevice d, int index, bool isOpen, NetBand band, string hide, bool force)
        {
            Dev = d;
            if (band != lastBand || hide != lastHide) Layout(band, hide);
            ThemedRow.Apply(Box, index);
            var theme = ThemeService.Current;
            bool blocks = theme.RowStyle == RowStyle.Blocks;
            summary.Opacity = d.Online ? 1 : 0.5;
            icon.Glyph = NetView.KindGlyph(d.Kind);
            nameTb.Text = d.Name;
            tagTb.Text = d.IsMe ? L("This PC") : d.IsRouter ? L("Router") : "";
            tagTb.Visibility = tagTb.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            newBadge.Visibility = d.IsNew ? Visibility.Visible : Visibility.Collapsed;
            newTb.Foreground = B("FuseboxGraph");
            newBadge.Background = B("FuseboxGraphFill");   // accent at 15 % (the Mac uses 18 %)
            lastTb.Text = !d.Online && d.LastSeen is { } ls ? L("last seen %@", NetView.AgoText(DateTime.UtcNow - ls)) : "";
            lastTb.Visibility = lastTb.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ipTb.Text = d.IpText;
            macTb.Text = d.MacText;
            macTb.FontSize = theme.Size(11);
            string remote = d.Remote && d.Mac == null ? L("Outside your own network: routers don't pass on hardware addresses, so the MAC address and maker can't be seen") : "";
            ToolTipService.SetToolTip(macTb, remote.Length > 0 ? remote : null);
            vendorTb.Text = d.VendorTitle;
            ToolTipService.SetToolTip(vendorTb, remote.Length > 0 ? remote : null);
            kindTb.Text = Devices.KindTitle(d.Kind);
            msTb.Text = d.Online ? NetView.MsText(d.Ms) : L("off");
            msTb.Foreground = (d.Loss ?? 0) > 0 && !blocks ? B("FuseboxWarn") : B("FuseboxOnRow");
            ToolTipService.SetToolTip(msTb, (d.Loss ?? 0) > 0 ? L("Some pings got no answer") : d.ViaTcp ? L("Measured with a TCP connection (no ping answer)") : null);
            FitName();
            if (isOpen) { var sig = Signature(d, band, hide); if (!open || sig != detailSig || force) { Details(d); detailSig = sig; } }
            else if (detailsPanel != null) { stack.Children.Remove(detailsPanel); detailsPanel = null; detailSig = ""; }
            open = isOpen;
        }

        static string Signature(NetDevice d, NetBand band, string hide) =>
            $"{d.Name}|{d.Ip}|{d.MacText}|{d.Vendor}|{d.Model}|{d.Kind}|{d.Ms}|{d.Loss}|{d.ViaTcp}|{d.Online}|{d.CheckingPorts}|{(d.Ports == null ? "-" : string.Join(',', d.Ports))}|{d.LastSeen}|{d.Custom}|{string.Join(',', d.Services)}|{d.Names.Count}|{hide}|{band}";

        /// <summary>Expanded lines (§6.9), in the spec's order.</summary>
        void Details(NetDevice d)
        {
            if (detailsPanel != null) stack.Children.Remove(detailsPanel);
            var panel = detailsPanel = new StackPanel { Spacing = 5, Margin = new Thickness(26, 6, 0, 2) };

            if (d.Names.Count > 0)
            {
                var first = d.Names[0];
                var t = first.Source == "Your name" ? L("Name from you") : L("Name from %@", NetNames.SourceTitle(first.Source));
                if (d.Names.Count > 1) t += L(" · also: ") + string.Join(", ", d.Names.Skip(1).Select(n => $"{n.Name} ({NetNames.SourceTitle(n.Source)})"));
                panel.Children.Add(Line("", t));
            }
            else panel.Children.Add(Line("", L("No name found; this is a guess from the maker")));
            if (d.Ip != 0) panel.Children.Add(Line("", d.Online ? L("IP address %@", d.IpText) : L("Last IP address %@", d.IpText)));
            bool macHidden = lastHide.Split(',', StringSplitOptions.TrimEntries).Contains("mac");
            if (d.Remote && d.Mac == null)
                panel.Children.Add(Line("", L("No hardware address or maker: this address is outside your own network, and routers don't pass those on")));
            else
            {
                if (!macHidden)   // the line follows the hide setting only, not the width
                     panel.Children.Add(Line("", d.Mac != null ? L("Hardware address %@", d.MacText) : L("Hardware address unknown")));
                if (d.Mac != null && d.PrivateMac) panel.Children.Add(Line("", L("Private address: a phone or laptop that hides its real address (Wi-Fi privacy). The maker can't be seen.")));
                else panel.Children.Add(Line("", d.Vendor == "–" ? L("Made by an unknown maker") : L("Made by %@", d.Vendor)));
            }
            if (d.Model != null) panel.Children.Add(Line("", L("Model: %@", d.Model)));
            panel.Children.Add(Line(NetView.KindGlyph(d.Kind), L("Looks like: %@", Devices.KindTitle(d.Kind).ToLowerInvariant())));
            if (d.Services.Count > 0)
                panel.Children.Add(Chips("", L("Offers"), d.Services.Select(NetNames.ServiceTitle).Distinct().Order().Select(s => (s, (string?)null)).ToList()));
            if (d.Ports != null)
            {
                if (d.Ports.Length == 0) panel.Children.Add(Line("", L("No common ports open")));
                else panel.Children.Add(Chips("", L("Open ports"), d.Ports.Select(x => ($"{Devices.PortName(x)} {x}", Devices.WebUrl(d.Ip, x))).ToList()));
            }
            if (d.Online && !d.IsMe) panel.Children.Add(Line("", NetView.LatencyText(d)));
            if (d.FirstSeen is { } f) panel.Children.Add(Line("", L("First seen %@", DateText(f))));
            if (d.LastSeen is { } l) panel.Children.Add(Line("", L("Last seen %@", DateText(l))));
            panel.Children.Add(Actions(d));
            stack.Children.Add(panel);
        }

        UIElement Chips(string glyph, string title, List<(string Text, string? Url)> items)
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var ic = Icon(glyph, 11); ic.VerticalAlignment = VerticalAlignment.Top; ic.Margin = new Thickness(0, 3, 0, 0);
            var wrap = new NetWrap();
            var head = Lbl(title + ":", 11, "FuseboxOnRowSecondary", wrap: false); head.VerticalAlignment = VerticalAlignment.Center;
            wrap.Children.Add(head);
            foreach (var (text, url) in items)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(Lbl(text, 10, "FuseboxOnRow", wrap: false));
                if (url != null)
                {
                    var link = new TextBlock { Text = L("Open"), Foreground = B("FuseboxGraph"), TextDecorations = TextDecorations.Underline };
                    Look.Label(link, 10);
                    var lb = p.Plain(link, url, double.NaN, double.NaN, (_, _) => Open(url));
                    row.Children.Add(lb);
                }
                var chip = new Border { Background = B("FuseboxGraphFill"), CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 2, 6, 2), Child = row };
                wrap.Children.Add(chip);
            }
            Grid.SetColumn(wrap, 1);
            g.Children.Add(ic); g.Children.Add(wrap);
            return g;
        }

        UIElement Actions(NetDevice d)
        {
            var wrap = new NetWrap { Gap = 6, Margin = new Thickness(0, 3, 0, 0) };
            if (d.Online && !d.IsMe && d.Ip != 0)
            {
                var b = p.Pill("", d.CheckingPorts ? L("Checking…") : d.Ports == null ? L("Check ports") : L("Check again"),
                    L("Try %lld common ports (web, file sharing, printing, …)", Devices.Ports.Length), (_, _) => p.CheckPorts(d));
                b.IsEnabled = !d.CheckingPorts;
                wrap.Children.Add(b);
            }
            if (d.Mac != null && !d.IsMe && !d.PrivateMac)
            {
                var fb = Lbl("", 10, "FuseboxOnRowSecondary", wrap: false); fb.VerticalAlignment = VerticalAlignment.Center;
                var b = p.Pill("", L("Wake on LAN"), L("Send a wake-up packet; works for computers and NAS that have Wake on LAN turned on"), async (_, _) =>
                    fb.Text = await p.Wake(d) ? L("Wake-up packet sent") : L("Couldn't send the wake-up packet"));
                wrap.Children.Add(b); wrap.Children.Add(fb);
            }
            if (d.Mac != null)
            {
                wrap.Children.Add(p.Pill("", L("Rename…"), null, (_, _) => p.Rename(d)));
                if (!d.IsMe) wrap.Children.Add(p.Pill("", L("Forget"), L("Remove from the remembered devices"), (_, _) => p.Forget(d)));
            }
            return wrap;
        }
    }
}
