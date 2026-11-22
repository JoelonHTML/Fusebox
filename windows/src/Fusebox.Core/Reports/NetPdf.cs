using System.Globalization;
using Fusebox.Core.Checkup;
using Fusebox.Core.Network;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Reports;

/// <summary>
/// The Network report as a light A4 PDF (translated): typical values and what stands out at the top, then connection, Wi-Fi, quality
/// statistics with the live reply-time graph, "Find the problem", speed test history. Only what the page already measured; no Wi-Fi name,
/// BSSID, MAC or IP addresses (and every text still passes the scrubber).
/// </summary>
public static class NetPdf
{
    static readonly CultureInfo C = CultureInfo.GetCultureInfo("en-US");
    static string F(double v) => v < 10 ? v.ToString("0.#", C) : Math.Round(v).ToString("0", C);

    /// <summary>Reply times to use for statistics: the live minute when it has a few values, else the 10-ping test.</summary>
    static Summary? Stats(IReadOnlyList<double>? live, PingStats? test) =>
        live is { Count: >= 5 } ? Summary.Of(live) :
        test is { Avg: { } avg } ? new Summary(test.Got, test.Min ?? avg, avg, avg, test.Max ?? avg, test.Max ?? avg) : null;

    public static byte[] Build(NetReportInput i, ScrubIdentity id, bool compress = true)
    {
        string Scrub(string s) => Scrubber.Scrub(s, id);
        var date = i.Now.ToString("D", Culture) + ", " + i.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var doc = new ReportPdf(L("Network report"), string.Join(" · ", new[] { date, i.Kind ?? L("not connected"), i.Pc }.Where(s => s.Length > 0)), Scrub, compress);

        var router = Stats(i.LiveRouter, i.Router);
        var inet = Stats(i.LiveInternet, i.Internet);
        var speeds = (i.Speeds ?? (i.Speed is { } one ? [one] : [])).ToList();
        var down = Summary.Of(speeds.Select(s => s.Down));
        var up = Summary.Of(speeds.Select(s => s.Up));

        // ---- typical values ----
        var tiles = new List<Tile>();
        string Range(Summary s) => L("range %@–%@ ms", F(s.Min), F(s.Max));
        if (router != null) tiles.Add(new(L("Router ping"), F(router.Median), "ms", Range(router)));
        if (inet != null) tiles.Add(new(L("Internet ping"), F(inet.Median), "ms", Range(inet)));
        if (i.DnsMs is { } dns) tiles.Add(new(L("DNS lookup"), F(dns), "ms", dns < 100 ? L("quick") : L("slow")));
        if (i.Rssi is { } rssi) tiles.Add(new(L("Wi-Fi signal"), rssi.ToString(C), "dBm", WifiMath.Word(rssi)));
        string Tests(int n) => n == 1 ? L("1 speed test") : L("middle of %lld speed tests", n);
        if (down != null) tiles.Add(new(L("Download"), F(down.Median), "Mbit/s", Tests(down.Count)));
        if (up != null) tiles.Add(new(L("Upload"), F(up.Median), "Mbit/s", Tests(up.Count)));
        if (tiles.Count < 6 && i.LinkMbps is { } link) tiles.Add(new(L("Link speed"), link.ToString(C), "Mbit/s"));
        doc.Glance(L("At a glance"), L("Typical values: the middle of what was measured"), tiles);

        // ---- what stands out ----
        var marks = new List<Mark>();
        if (i.Finding is { Ok: false } f) marks.Add(new(2, f.Text, f.Numbers));
        static int Lost(PingStats? t) => t is { Sent: > 0 } ? (int)Math.Round(t.Loss * 100) : 0;
        if (Lost(i.Router) > 0) marks.Add(new(Lost(i.Router) >= 20 ? 2 : 1, L("%lld%% of the replies from the router were lost", Lost(i.Router))));
        if (Lost(i.Internet) > 0) marks.Add(new(Lost(i.Internet) >= 20 ? 2 : 1, L("%lld%% of the pings to the internet were lost", Lost(i.Internet))));
        if (i.Rssi is { } r && r < -70) marks.Add(new(r < -75 ? 2 : 1, L("Weak Wi-Fi signal (%lld dBm)", r), L("Closer to the router, or fewer walls in between, usually helps.")));
        if (i.DnsMs is { } d && d >= 100) marks.Add(new(d >= 300 ? 2 : 1, L("Slow DNS lookups (%@ ms)", F(d)), L("Websites take longer to start loading.")));
        foreach (var (name, s) in new[] { (L("Router"), router), (L("Internet"), inet) })
            if (s is { Count: >= 5 } && s.Max >= Math.Max(3 * s.Median, s.Median + 40))
                marks.Add(new(1, L("%@: reply times jump to %@ ms (typical %@ ms)", name, F(s.Max), F(s.Median))));
        if (i.Internet?.Jitter is { } j && j >= 30) marks.Add(new(1, L("Uneven reply times (jitter %@ ms): calls and games can stutter", F(j))));
        var last = speeds.FirstOrDefault();
        if (last is { Ping: { } idle, LoadedPing: { } busy } && busy - idle >= 30)
            marks.Add(new(busy - idle >= 100 ? 2 : 1, L("Delay rises to %@ ms while the line is busy (bufferbloat)", F(busy)), L("Normally %@ ms.", F(idle))));
        if (last?.Variation is >= 0.25) marks.Add(new(1, L("The speed was unsteady during the last test")));
        if (last != null && down is { Count: >= 3 } && last.Down < 0.6 * down.Median)
            marks.Add(new(1, L("The last speed test (%@ Mbit/s) is well below the usual %@ Mbit/s", F(last.Down), F(down.Median))));
        if (i.Vpn == true) marks.Add(new(0, L("A VPN is on: all numbers go through it")));
        if (tiles.Count + marks.Count > 0)   // nothing measured: nothing to call unusual either
            doc.StandsOut(L("Stands out"), L("Unusual values, most important first"), marks, L("Nothing unusual: no lost replies, slow lookups or weak signal."));

        // ---- connection and Wi-Fi ----
        string YesNo(bool b) => b ? L("yes") : L("no");
        var facts = new List<(string, string)> { (L("Type"), i.Kind ?? L("not connected")) };
        if (i.ProfileCategory is >= 0 and <= 2) facts.Add((L("Network profile"), i.ProfileCategory switch { 0 => L("Public"), 1 => L("Private"), _ => L("Domain") }));
        if (i.Vpn is { } vpn) facts.Add(("VPN", YesNo(vpn)));
        if (i.EncryptedDns is { } doh) facts.Add((L("Encrypted DNS"), doh ? L("on") : L("off")));
        if (i.Metered is { } m) facts.Add((L("Metered"), YesNo(m)));
        if (i.Ipv6Internet is { } v6) facts.Add((L("IPv6 internet"), YesNo(v6)));
        if (i.Devices is { } dev) facts.Add((L("Devices found"), dev.ToString(C)));
        doc.Section(L("Connection"));
        doc.Facts(facts);
        if (i.Rssi != null || i.LinkMbps != null || i.Channel != null || i.Standard != null)
        {
            var wf = new List<(string, string)>();
            if (i.Rssi is { } rs) wf.Add((L("Signal"), $"{rs} dBm ({WifiMath.Word(rs)})"));
            if (i.LinkMbps is { } l) wf.Add((L("Link speed"), $"{l} Mbit/s"));
            if (i.Channel is { } ch) wf.Add((L("Channel"), ch.ToString(C)));
            if (i.Band != null) wf.Add((L("Band"), i.Band));
            if (i.WidthMhz is { } w) wf.Add((L("Channel width"), $"{w} MHz"));
            if (i.Standard != null) wf.Add((L("Standard"), i.Standard));
            doc.Section("Wi-Fi");
            doc.Facts(wf);
            if (i.Rssi is { } sig) SignalBar(doc, sig);
        }

        // ---- quality ----
        var rows = new List<string[]>();
        foreach (var (name, s, test) in new[] { (L("Router"), router, i.Router), (L("Internet"), inet, i.Internet) })
        {
            if (s == null) { if (test != null) rows.Add([name, "–", "–", "–", "–", "–", "–", L("no answer")]); continue; }
            rows.Add([name, F(s.Min), F(s.Median), F(s.Avg), F(s.P95), F(s.Max), test?.Jitter is { } jt ? F(jt) : "–",
                test is { Sent: > 0 } ? $"{(int)Math.Round(test.Loss * 100)}%" : "–"]);
        }
        if (i.DnsMs is { } dn) rows.Add([L("DNS lookup"), "", F(dn), "", "", "", "", ""]);
        if (rows.Count > 0 || i.LiveRouter is { Count: >= 2 } || i.LiveInternet is { Count: >= 2 }) doc.Section(L("Quality"));
        doc.Table([L("Reply time (ms)"), L("Lowest"), L("Median"), L("Average"), "95%", L("Highest"), L("Jitter"), L("Lost")], rows,
            [0.24, 0.1, 0.1, 0.1, 0.1, 0.1, 0.13, 0.13]);
        var series = new List<Series>();
        if (i.LiveInternet is { Count: >= 2 } li) series.Add(new(L("Internet"), ReportPdf.Accent, Seconds(li), MarkMax: true));
        if (i.LiveRouter is { Count: >= 2 } lr) series.Add(new(L("Router"), Rgb.Gray(0.45), Seconds(lr), Fill: series.Count == 0, Dashed: series.Count > 0, MarkMax: series.Count == 0));
        if (series.Count > 0)
        {
            int n = series.Max(s => s.Points.Count);
            var ticks = new List<(double, string)>();
            for (int t = -(n - 1) / 15 * 15; t < 0; t += 15) ticks.Add((t, $"{t} s"));
            ticks.Add((0, L("now")));
            doc.Chart(L("Reply times, last %lld seconds", n), -(n - 1), 0, ticks, v => F(v) + " ms", series,
                caption: L("One ping per second while the Network page was open. A lost reply repeats the value before it."));
        }

        // ---- find the problem ----
        if (i.Finding is { } fp)
        {
            doc.Section(L("Find the problem"));
            doc.Bullet(fp.Text, fp.Ok == false ? ReportPdf.Danger : fp.Ok == true ? ReportPdf.Good : ReportPdf.Warn, fp.Numbers);
        }

        // ---- speed tests ----
        if (speeds.Count > 0)
        {
            doc.Section(L("Speed tests"));
            var old = Enumerable.Reverse(speeds).ToList();
            if (old.Count > 1)
                doc.Bars(L("Last %lld speed tests (Mbit/s)", old.Count), old.Select(s => s.Date.ToLocalTime().ToString("d MMM", Culture)).ToList(),
                    [(L("Download"), ReportPdf.Accent, old.Select(s => s.Down).ToArray()), (L("Upload"), ReportPdf.Accent.Tint(0.4), old.Select(s => s.Up).ToArray())],
                    F, valueOnTop: true);
            if (down is { Count: > 1 } && up != null)
                doc.Table([L("Speed (Mbit/s)"), L("Lowest"), L("Median"), L("Average"), L("Highest")],
                    [[L("Download"), F(down.Min), F(down.Median), F(down.Avg), F(down.Max)], [L("Upload"), F(up.Min), F(up.Median), F(up.Avg), F(up.Max)]],
                    [0.36, 0.16, 0.16, 0.16, 0.16]);
            var s = speeds[0];
            var lf = new List<(string, string)>
            {
                (L("Last test"), s.Date.ToLocalTime().ToString("d MMM yyyy HH:mm", Culture) + " · " + SpeedHistory.KindName(s.Kind)),
                (L("Download"), $"{F(s.Down)} Mbit/s" + (s.DownPeak is { } dp ? " " + L("(top %@)", F(dp)) : "")),
                (L("Upload"), $"{F(s.Up)} Mbit/s" + (s.UpPeak is { } upk ? " " + L("(top %@)", F(upk)) : "")),
            };
            if (s.Ping is { } p) lf.Add((L("Delay"), $"{F(p)} ms" + (s.LoadedPing is { } lp ? " · " + L("%@ ms while busy", F(lp)) : "")));
            if (s.Ping is { } a && s.LoadedPing is { } b)
            {
                var x = Math.Max(0, b - a);
                lf.Add((L("Bufferbloat"), (x < 30 ? L("none") : x < 100 ? L("some") : L("heavy")) + $" (+{F(x)} ms)"));
            }
            if (s.Variation is { } v) lf.Add((L("Stability"), v < 0.1 ? L("steady") : v < 0.25 ? L("varies a little") : L("unsteady")));
            doc.Facts(lf);
        }

        return doc.Save(L("Fusebox · Network report · %@", i.Now.ToString("d MMM yyyy", Culture)), L("Contains no Wi-Fi name, network addresses, computer name or user name."),
            L("Network report"));
    }

    static List<(double X, double Y)> Seconds(IReadOnlyList<double> v) => v.Select((y, k) => ((double)(k - (v.Count - 1)), y)).ToList();

    /// <summary>A small scale from −90 to −30 dBm with the weak / fair / good bands and a marker at the current signal.</summary>
    static void SignalBar(ReportPdf doc, int rssi) =>
        doc.Timeline(L("Signal strength (dBm)"), -90, -30, [(-90, "−90"), (-80, "−80"), (-70, "−70"), (-60, "−60"), (-50, "−50"), (-40, "−40"), (-30, "−30")],
            [(-90, -70, ReportPdf.Danger.Tint(0.25)), (-70, -60, ReportPdf.Warn.Tint(0.3)), (-60, -30, ReportPdf.Good.Tint(0.3))],
            [(Math.Clamp(rssi, -90, -30), ReportPdf.Ink)], [(L("weak"), ReportPdf.Danger.Tint(0.5)), (L("fair"), ReportPdf.Warn.Tint(0.5)), (L("good"), ReportPdf.Good.Tint(0.5))]);
}
