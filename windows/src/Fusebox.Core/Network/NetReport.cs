using System.Globalization;
using System.Text;
using Fusebox.Core.Checkup;

namespace Fusebox.Core.Network;

/// <summary>What the Network page already knows (nothing new is measured). Speeds = history newest first; Live* = the graph's last minute of
/// reply times (1 per second); Pc = processor, memory and Windows version (no names) for the PDF header. No Wi-Fi name, BSSID or other devices' addresses go in here on purpose.</summary>
public sealed record NetReportInput(
    DateTime Now, string? Kind, bool? Vpn = null, int? ProfileCategory = null, bool? EncryptedDns = null, bool? Metered = null,
    int? Rssi = null, int? LinkMbps = null, int? Channel = null, string? Band = null, int? WidthMhz = null, string? Standard = null,
    PingStats? Router = null, PingStats? Internet = null, double? DnsMs = null, bool? Ipv6Internet = null,
    Finding? Finding = null, SpeedEntry? Speed = null, int? Devices = null,
    IReadOnlyList<SpeedEntry>? Speeds = null, IReadOnlyList<double>? LiveRouter = null, IReadOnlyList<double>? LiveInternet = null, string Pc = "");

/// <summary>Plain-English network report for a helpdesk or provider (English on purpose, like the Checkup report). Sections without data are left out.</summary>
public static class NetReport
{
    public const string Note = "Contains no Wi-Fi name, network addresses, computer name or user name.";
    static readonly CultureInfo C = CultureInfo.GetCultureInfo("en-US");

    static string F(double v) => v < 10 ? v.ToString("0.#", C) : Math.Round(v).ToString("0", C);
    static string YesNo(bool b) => b ? "yes" : "no";

    static string Ping(PingStats? s)
    {
        if (s == null) return "not measured";
        if (s.Got == 0 || s.Avg is not { } avg) return "no answer";
        var t = $"{F(avg)} ms";
        if (s.Min is { } lo && s.Max is { } hi) t += $" (min {F(lo)}, max {F(hi)})";
        if (s.Jitter is { } j) t += $", jitter {F(j)} ms";
        return t + $", {(int)Math.Round(s.Loss * 100)}% lost";
    }

    public static string Build(NetReportInput i, ScrubIdentity id)
    {
        var sb = new StringBuilder();
        void Line(string k, string v) => sb.Append(k).Append(": ").AppendLine(v);
        void Head(string h) => sb.AppendLine().AppendLine(h);
        sb.AppendLine("Fusebox network report").AppendLine("Created " + i.Now.ToString("ddd d MMM yyyy HH:mm", C));

        Head("CONNECTION");
        Line("Type", i.Kind ?? "not connected");
        if (i.ProfileCategory is { } pc) Line("Network profile", pc switch { 0 => "public", 1 => "private", 2 => "domain", _ => "unknown" });
        if (i.Vpn is { } vpn) Line("VPN", YesNo(vpn));
        if (i.EncryptedDns is { } doh) Line("Encrypted DNS", doh ? "on" : "off");
        if (i.Metered is { } m) Line("Metered", YesNo(m));
        if (i.Ipv6Internet is { } v6) Line("IPv6 internet", YesNo(v6));

        if (i.Rssi != null || i.LinkMbps != null || i.Channel != null || i.Standard != null)
        {
            Head("WI-FI");
            if (i.Rssi is { } r) Line("Signal", $"{r} dBm ({(r >= -60 ? "good" : r >= -70 ? "fair" : "weak")})");
            if (i.LinkMbps is { } l) Line("Link speed", $"{l} Mbit/s");
            if (i.Channel is { } ch) Line("Channel", ch.ToString(C));
            if (i.Band != null) Line("Band", i.Band);
            if (i.WidthMhz is { } w) Line("Channel width", $"{w} MHz");
            if (i.Standard != null) Line("Standard", i.Standard);
        }

        Head("QUALITY (10 pings)");
        Line("Router", Ping(i.Router));
        Line("Internet", Ping(i.Internet));
        if (i.DnsMs is { } d) Line("DNS lookup", $"{F(d)} ms");

        if (i.Finding is { } f)
        {
            Head("FIND THE PROBLEM");
            sb.AppendLine(f.Text);
            if (f.Numbers.Length > 0) sb.AppendLine(f.Numbers);
        }

        if (i.Speed is { } s)
        {
            Head("SPEED TEST");
            Line("Tested", s.Date.ToLocalTime().ToString("d MMM yyyy HH:mm", C) + (s.Kind is { } k ? $" ({k.ToString().ToLowerInvariant()})" : ""));
            Line("Download", $"{F(s.Down)} Mbit/s" + (s.DownPeak is { } dp ? $" (top {F(dp)})" : ""));
            Line("Upload", $"{F(s.Up)} Mbit/s" + (s.UpPeak is { } up ? $" (top {F(up)})" : ""));
            if (s.Ping is { } p) Line("Delay", $"{F(p)} ms" + (s.LoadedPing is { } lp ? $", {F(lp)} ms while busy" : ""));
            if (s.Ping is { } idle && s.LoadedPing is { } busy)
            {
                var x = Math.Max(0, busy - idle);
                Line("Bufferbloat", (x < 30 ? "none" : x < 100 ? "some" : "heavy") + $" (+{F(x)} ms)");
            }
            if (s.Variation is { } v) Line("Stability", v < 0.1 ? "steady" : v < 0.25 ? "varies a little" : "unsteady");
        }

        if (i.Devices is { } n) { Head("NETWORK"); Line("Devices found", n.ToString(C)); }

        sb.AppendLine().AppendLine(Note);
        return Scrubber.Scrub(sb.ToString(), id);
    }
}
