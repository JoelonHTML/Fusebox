using Fusebox.Core.Network;
using Fusebox.Platform.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>Speed test (§6.4) and public IP (§6.5): both only after a click, both cancelled when the page is left.</summary>
public sealed partial class NetworkPage
{
    StackPanel? speedBox;
    TextBlock? spPhase, spNow;
    ProgressBar? spBar;
    SpeedKind? speedConfirm;
    bool speedChecking, speedRunning;
    string? speedPhase, speedError;
    double speedProgress;
    double? speedLive;
    List<SpeedEntry> history = [];
    PublicIp? publicIp;
    string? publicIpState;
    CancellationTokenSource? speedCts, ipCts;

    static string Mbit(double mbit) => SpeedHistory.MbitText(mbit, Prefs.Bool("speedMB"));

    void StopSpeed()
    {
        speedCts?.Cancel(); speedCts = null;
        speedChecking = speedRunning = false; speedPhase = null; speedLive = null; speedConfirm = null;
    }

    void RenderSpeed()
    {
        if (speedBox == null) return;
        speedBox.Children.Clear();
        spPhase = spNow = null; spBar = null;
        var last = history.FirstOrDefault();

        if (speedRunning)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
            spPhase = Lbl(speedPhase ?? "", 11, "FuseboxOnRow"); spPhase.MaxWidth = 220;
            row.Children.Add(spPhase);
            row.Children.Add(Pill("", L("Cancel"), null, (_, _) => CancelSpeed()));
            speedBox.Children.Add(row);
            spBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = speedProgress };
            speedBox.Children.Add(spBar);
            var now = Kv(L("Now"), speedLive is { } l ? Mbit(l) : "–");
            spNow = (TextBlock)now.Children[1];
            speedBox.Children.Add(now);
        }
        else if (speedConfirm is { } kind)
        {
            var warn = Lbl(L("This connection is metered (mobile data, iPhone hotspot or Low Data Mode). The %@ test uses %@ of data.", KindName(kind).ToLowerInvariant(), SpeedHistory.Usage(kind, last)), 11, "FuseboxWarn");
            warn.Foreground = WarnBrush();
            speedBox.Children.Add(warn);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(Pill("", L("Run anyway"), null, (_, _) => { speedConfirm = null; RunSpeed(kind); }));
            row.Children.Add(Pill("", L("Not now"), null, (_, _) => { speedConfirm = null; RenderSpeed(); }));
            speedBox.Children.Add(row);
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var quick = Pill("", L("Quick"), L("About 10 s, 4 connections at once. The first 2 s, while the line speeds up, don't count."), (_, _) => RequestSpeed(SpeedKind.Quick));
            var full = Pill("", L("Full"), L("About 30 s, 6 connections: typical and top speed, delay while the line is busy (bufferbloat) and how steady it is."), (_, _) => RequestSpeed(SpeedKind.Full));
            quick.IsEnabled = full.IsEnabled = !speedChecking;
            row.Children.Add(quick); row.Children.Add(full);
            speedBox.Children.Add(row);
            speedBox.Children.Add(Lbl(L("Quick uses %@, Full %@ (via Cloudflare)", SpeedHistory.Usage(SpeedKind.Quick, last), SpeedHistory.Usage(SpeedKind.Full, last)), 10, "FuseboxOnRowSecondary"));
        }
        if (speedError != null) { var e = Lbl(speedError, 11, "FuseboxWarn"); e.Foreground = WarnBrush(); speedBox.Children.Add(e); }

        if (last != null)
        {
            speedBox.Children.Add(Kv(L("Download"), Mbit(last.Down) + (last.DownPeak is { } dp ? L(" · top %@", Mbit(dp)) : "")));
            speedBox.Children.Add(Kv(L("Upload"), Mbit(last.Up) + (last.UpPeak is { } up ? L(" · top %@", Mbit(up)) : "")));
            speedBox.Children.Add(Kv(L("Delay"), last.Ping is { } p ? NetView.MsText(p) + " ms" + (last.LoadedPing is { } lp ? L(" · busy %@ ms", NetView.MsText(lp)) : "") : "–"));
            if (last.Variation is { } v) speedBox.Children.Add(Kv(L("Stability"), SpeedMath.StabilityText(v)));
            speedBox.Children.Add(Kv(L("Tested"), DateText(last.Date) + " · " + SpeedHistory.KindName(last.Kind)));
            if (last.Ping is { } idle && last.LoadedPing is { } busy)
            {
                var lv = SpeedMath.LoadVerdict(idle, busy);
                speedBox.Children.Add(Line(OkGlyph(lv.Ok), lv.Text, OkBrush(lv.Ok)));
            }
            if (history.Count > 1) speedBox.Children.Add(HistoryBars());
            if (!speedRunning) { var save = SaveReportPill(); save.HorizontalAlignment = HorizontalAlignment.Left; speedBox.Children.Add(save); }
        }

        // public IP: only on click, never stored
        var rule = new Border { Height = 1, Opacity = 0.4, Background = B("FuseboxOnRowSecondary"), Margin = new Thickness(0, 2, 0, 2) };
        speedBox.Children.Add(rule);
        if (publicIp is { } ip)
        {
            speedBox.Children.Add(Kv(L("Public IP"), ip.Ip));
            if (ip.Isp != null) speedBox.Children.Add(Kv(L("Provider"), ip.Isp));
            if (ip.Place != null) speedBox.Children.Add(Kv(L("Location"), ip.Place));
        }
        else
        {
            var b = Pill("", publicIpState ?? L("Show public IP"), L("Asks speed.cloudflare.com which address and provider you appear as"), (_, _) => LoadPublicIp());
            b.HorizontalAlignment = HorizontalAlignment.Left;
            speedBox.Children.Add(b);
        }
    }

    static string KindName(SpeedKind k) => k == SpeedKind.Quick ? L("Quick") : L("Full");

    static string DateText(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Last results as bars (download), oldest left; newest 100 %, others 45 %; wide = Full. 26 DIPs tall.</summary>
    UIElement HistoryBars()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 26, VerticalAlignment = VerticalAlignment.Bottom };
        var heights = SpeedHistory.BarHeights(history);
        for (int i = 0; i < heights.Length; i++)
        {
            var e = history[heights.Length - 1 - i];
            double w = e.Kind == SpeedKind.Full ? 9 : 5;
            var bar = new Microsoft.UI.Xaml.Controls.Border
            {
                Width = w, Height = heights[i], CornerRadius = new CornerRadius(w / 2), Background = B("FuseboxGraph"),
                Opacity = i == heights.Length - 1 ? 1 : 0.45, VerticalAlignment = VerticalAlignment.Bottom,
            };
            ToolTipService.SetToolTip(bar, $"{DateText(e.Date)} · {SpeedHistory.KindName(e.Kind)}: ↓{Mbit(e.Down)} ↑{Mbit(e.Up)}");
            row.Children.Add(bar);
        }
        var cap = Lbl(L("last %lld · wide = Full", heights.Length), 9, "FuseboxOnRowSecondary", wrap: false);
        cap.Margin = new Thickness(4, 0, 0, 0); cap.VerticalAlignment = VerticalAlignment.Bottom;
        row.Children.Add(cap);
        return row;
    }

    // ---- run ----

    /// <summary>Metered connection (mobile data, hotspot, data limit)? Then ask first.</summary>
    async void RequestSpeed(SpeedKind kind)
    {
        if (speedRunning || speedChecking || speedConfirm != null) return;
        speedError = null; speedChecking = true;
        RenderSpeed();
        var metered = await Task.Run(WinNet.IsMetered);
        speedChecking = false;
        if (!visible) return;
        if (metered) { speedConfirm = kind; RenderSpeed(); }
        else RunSpeed(kind);
    }

    async void RunSpeed(SpeedKind kind)
    {
        if (speedRunning) return;
        var cts = speedCts = new CancellationTokenSource();
        speedRunning = true; speedError = null; speedLive = null; speedProgress = 0; speedPhase = L("Measuring delay…");
        RenderSpeed();
        try
        {
            var r = await Task.Run(() => SpeedTest.RunAsync(kind, (phase, live, p) => UI(() =>
            {
                if (cts.IsCancellationRequested) return;
                speedPhase = phase; speedLive = live; speedProgress = p;
                if (spPhase != null) spPhase.Text = phase;
                if (spBar != null) spBar.Value = p;
                if (spNow != null) spNow.Text = live is { } l ? Mbit(l) : "–";
            }), cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !visible) return;
            history = SpeedHistory.Push(history, new SpeedEntry(DateTime.UtcNow, r.Down, r.Up, r.Ping, kind, r.DownPeak, r.UpPeak, r.LoadedPing, r.Variation));
            Prefs.Set("speedHistory", SpeedHistory.ToJson(history));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            // Our own messages (Check) are already plain words; anything else is a connection problem, not raw .NET text.
            speedError = ex is HttpRequestException { StatusCode: not null } ? ex.Message
                : L("No connection to Cloudflare's speed test. Check your internet connection and try again.");
        }
        catch { }
        finally
        {
            if (speedCts == cts) { speedCts = null; speedRunning = false; speedPhase = null; speedLive = null; if (visible) RenderSpeed(); }
        }
    }

    void CancelSpeed()
    {
        speedCts?.Cancel(); speedCts = null;
        speedRunning = false; speedPhase = null; speedLive = null;
        RenderSpeed();
    }

    // ---- public IP (click only; gone after leaving the tab) ----

    async void LoadPublicIp()
    {
        if (ipCts != null) return;
        var cts = ipCts = new CancellationTokenSource();
        publicIpState = L("Asking Cloudflare…");
        RenderSpeed();
        try
        {
            var ip = await Task.Run(() => SpeedTest.MetaAsync(cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !visible) return;
            if (ip == null) publicIpState = L("Couldn't reach Cloudflare"); else { publicIp = ip; publicIpState = null; }
        }
        catch (OperationCanceledException) { return; }
        catch { publicIpState = L("Couldn't reach Cloudflare"); }
        finally { if (ipCts == cts) { ipCts = null; if (visible) RenderSpeed(); } }
    }
}
