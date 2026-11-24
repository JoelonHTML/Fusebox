using Fusebox.Core.Network;
using Fusebox.Platform.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>Speed test card (§6.4) and public IP (§6.5, shown in the Connection card): both only after a click, both cancelled when the page is left.</summary>
public sealed partial class NetworkPage
{
    TextBlock? spPhase, spNow;
    ProgressBar? spBar;
    SpeedKind? speedConfirm;
    bool clearAsk;   // "Clear history…" asks once, inline (no dialog)
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
        speedChecking = speedRunning = false; speedPhase = null; speedLive = null; speedConfirm = null; clearAsk = false;
    }

    /// <summary>Speed test card: last result as big numbers, delay, when; Quick / Full (or progress, or the metered question); Save report… and Clear history… once there is a history.
    /// Details (per card pref): top speeds, stability, bufferbloat verdict, history bars.</summary>
    void RenderSpeed()
    {
        if (!built || cards.Length != 3) return;
        var c = cards[2];
        c.Face.Children.Clear(); c.Details.Children.Clear();
        spPhase = spNow = null; spBar = null;
        c.OpenKey = history.Count == 0 ? null : "netSpeedDetails";
        var last = history.FirstOrDefault();
        var f = c.Face;

        if (last != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            row.Children.Add(Stat(NetTerm.Download, BigSplit(Mbit(last.Down)), "↓ " + L("Download")));
            row.Children.Add(Stat(NetTerm.Upload, BigSplit(Mbit(last.Up)), "↑ " + L("Upload")));
            f.Children.Add(row);
            if (last.Ping is { } p)
            {
                var d = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                d.Children.Add(Term(NetTerm.Delay, null, 11));
                d.Children.Add(Lbl(NetView.MsText(p) + " ms", 11, "FuseboxOnRow", wrap: false));
                if (last.LoadedPing is { } b)
                {
                    bool bad = SpeedMath.LoadVerdict(p, b).Ok == false;
                    d.Children.Add(Lbl("·", 11, "FuseboxOnRowSecondary", wrap: false));
                    d.Children.Add(Term(NetTerm.BusyDelay, L("busy"), 11));
                    var bt = Lbl(NetView.MsText(b) + " ms", 11, "FuseboxOnRow", wrap: false);
                    if (bad) bt.Foreground = WarnBrush();
                    d.Children.Add(bt);
                }
                f.Children.Add(d);
            }
            f.Children.Add(Lbl(DateText(last.Date) + " · " + SpeedHistory.KindName(last.Kind), 10, "FuseboxOnRowSecondary"));
        }
        else if (!speedRunning) f.Children.Add(Lbl(L("No speed test yet"), 12, "FuseboxOnRowSecondary"));

        if (speedRunning)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
            spNow = Num(speedLive is { } l ? Mbit(l) : "", 18); spNow.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(spNow);
            row.Children.Add(Pill("\uE711", L("Cancel"), null, (_, _) => CancelSpeed()));
            f.Children.Add(row);
            spBar = new ProgressBar { Minimum = 0, Maximum = 1, Value = speedProgress };
            f.Children.Add(spBar);
            spPhase = Lbl(speedPhase ?? "", 10, "FuseboxOnRowSecondary");
            f.Children.Add(spPhase);
        }
        else if (speedConfirm is { } kind)
        {
            var warn = Lbl(L("This connection is metered (mobile data, iPhone hotspot or Low Data Mode). The %@ test uses %@ of data.", KindName(kind).ToLowerInvariant(), SpeedHistory.Usage(kind, last)), 11, "FuseboxWarn");
            warn.Foreground = WarnBrush();
            f.Children.Add(warn);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(Pill("\uEC4A", L("Run anyway"), null, (_, _) => { speedConfirm = null; RunSpeed(kind); }));
            row.Children.Add(Pill("\uE711", L("Not now"), null, (_, _) => { speedConfirm = null; RenderSpeed(); }));
            f.Children.Add(row);
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var quick = Pill("\uE945", L("Quick"), L("About 10 s, 4 connections at once. The first 2 s, while the line speeds up, don't count."), (_, _) => RequestSpeed(SpeedKind.Quick));
            var full = Pill("\uEC4A", L("Full"), L("About 30 s, 6 connections: typical and top speed, delay while the line is busy (bufferbloat) and how steady it is."), (_, _) => RequestSpeed(SpeedKind.Full));
            quick.IsEnabled = full.IsEnabled = !speedChecking;
            row.Children.Add(quick); row.Children.Add(full);
            f.Children.Add(row);
            f.Children.Add(Lbl(L("Quick uses %@, Full %@ (via Cloudflare)", SpeedHistory.Usage(SpeedKind.Quick, last), SpeedHistory.Usage(SpeedKind.Full, last)), 9.5, "FuseboxOnRowSecondary"));
        }
        if (speedError != null) { var e = Lbl(speedError, 10, "FuseboxWarn"); e.Foreground = WarnBrush(); f.Children.Add(e); }

        if (last != null && !speedRunning)
        {
            if (clearAsk)
            {
                f.Children.Add(Lbl(L("Delete all %lld saved speed tests? Fastest and average then start fresh.", history.Count), 11, "FuseboxOnRow"));
                var ask = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                ask.Children.Add(Pill("\uE74D", L("Delete"), null, (_, _) => ClearHistory()));
                ask.Children.Add(Pill("\uE711", L("Not now"), null, (_, _) => { clearAsk = false; RenderSpeed(); }));
                f.Children.Add(ask);
            }
            else
            {
                var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
                pills.Children.Add(SaveReportPill());
                pills.Children.Add(Pill("\uE74D", L("Clear history…"), L("Deletes the saved speed tests, for example after your connection got faster, so fastest and average start fresh."),
                    (_, _) => { clearAsk = true; RenderSpeed(); }));
                f.Children.Add(pills);
            }
        }

        // details
        if (last != null)
        {
            var d = c.Details;
            if (last.DownPeak is { } dp) d.Children.Add(Kv(NetTerm.Download, L("top %@", Mbit(dp))));
            if (last.UpPeak is { } up) d.Children.Add(Kv(NetTerm.Upload, L("top %@", Mbit(up))));
            if (last.Variation is { } v) d.Children.Add(Kv(NetTerm.Stability, SpeedMath.StabilityText(v)));
            if (last.Ping is { } idle && last.LoadedPing is { } busy)
            {
                var lv = SpeedMath.LoadVerdict(idle, busy);
                var line = Line(OkGlyph(lv.Ok), lv.Text, OkBrush(lv.Ok)); line.Margin = new Thickness(0, 2, 0, 0);
                d.Children.Add(line);
            }
            if (history.Count > 1) { var bars = HistoryBars(); bars.Margin = new Thickness(0, 4, 0, 0); d.Children.Add(bars); }
        }
        ApplyOpen(c);
    }

    static string KindName(SpeedKind k) => k == SpeedKind.Quick ? L("Quick") : L("Full");

    static string DateText(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Last results as bars (download), oldest left; newest 100 %, others 45 %; wide = Full. 26 DIPs tall.</summary>
    FrameworkElement HistoryBars()
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
                if (spNow != null) spNow.Text = live is { } l ? Mbit(l) : "";
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

    void ClearHistory()
    {
        clearAsk = false;
        history = [];
        Prefs.Set("speedHistory", SpeedHistory.ToJson(history));
        RenderSpeed();
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
        RenderConnection();
        try
        {
            var ip = await Task.Run(() => SpeedTest.MetaAsync(cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !visible) return;
            if (ip == null) publicIpState = L("Couldn't reach Cloudflare"); else { publicIp = ip; publicIpState = null; }
        }
        catch (OperationCanceledException) { return; }
        catch { publicIpState = L("Couldn't reach Cloudflare"); }
        finally { if (ipCts == cts) { ipCts = null; if (visible) RenderConnection(); } }
    }
}
