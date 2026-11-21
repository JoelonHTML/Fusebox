using System.Globalization;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Network;

/// <summary>Width bands of the page (§6.1): narrow &lt; 470 · vendor 470–599 · kind 600–759 · mac ≥ 760.</summary>
public enum NetBand { Narrow, Vendor, Kind, Mac }
public enum NetCol { Name, Ip, Mac, Vendor, Kind, Ms }
public enum NetFilter { All, Online, New, Offline }

/// <summary>Layout, filter, sort and texts of the device list (pure; the page only draws).</summary>
public static class NetView
{
    public static NetBand Band(double width) => width >= 760 ? NetBand.Mac : width >= 600 ? NetBand.Kind : width >= 470 ? NetBand.Vendor : NetBand.Narrow;

    /// <summary>Column widths (DIPs) of the fixed columns; Name fills the rest.</summary>
    public static double Width(NetCol c) => c switch { NetCol.Ip => 104, NetCol.Mac => 124, NetCol.Vendor => 118, NetCol.Kind => 104, NetCol.Ms => 42, _ => 0 };

    static bool Hidden(string hide, string id) => hide.Split(',', StringSplitOptions.TrimEntries).Contains(id);

    /// <summary>A column shows when the window is wide enough and it is not hidden (Name, IP and ms never hide).</summary>
    public static bool Shows(NetBand band, string hide, NetCol c) => c switch
    {
        NetCol.Mac => band >= NetBand.Mac && !Hidden(hide, "mac"),
        NetCol.Vendor => band >= NetBand.Vendor && !Hidden(hide, "vendor"),
        NetCol.Kind => band >= NetBand.Kind && !Hidden(hide, "kind"),
        _ => true,
    };

    public static string FilterTitle(NetFilter f) => f switch { NetFilter.Online => L("Online"), NetFilter.New => L("New"), NetFilter.Offline => L("Offline"), _ => L("All") };

    public static bool Matches(NetFilter f, NetDevice d) => f switch { NetFilter.Online => d.Online, NetFilter.New => d.IsNew, NetFilter.Offline => !d.Online, _ => true };

    /// <summary>Online devices always before offline ones; inside a group by the column (natural compare for names and makers, IP numeric, MAC unknown last, ms unknown = ∞).</summary>
    public static List<NetDevice> Sort(IEnumerable<NetDevice> list, NetCol col, bool ascending)
    {
        int Cmp(NetDevice a, NetDevice b) => col switch
        {
            NetCol.Name => Natural(a.Name, b.Name),
            NetCol.Mac => string.CompareOrdinal(a.Mac == null ? "~" : a.MacText, b.Mac == null ? "~" : b.MacText),
            NetCol.Vendor => Natural(a.VendorTitle, b.VendorTitle),
            NetCol.Kind => string.CompareOrdinal(Devices.KindTitle(a.Kind), Devices.KindTitle(b.Kind)),
            NetCol.Ms => (a.Ms ?? double.PositiveInfinity).CompareTo(b.Ms ?? double.PositiveInfinity),
            _ => a.Ip.CompareTo(b.Ip),
        };
        var items = list.ToList();
        // stable: List.Sort is not, so index breaks ties
        var idx = Enumerable.Range(0, items.Count).ToArray();
        Array.Sort(idx, (x, y) =>
        {
            var a = items[x]; var b = items[y];
            if (a.Online != b.Online) return a.Online ? -1 : 1;
            int c = Cmp(a, b);
            if (!ascending) c = -c;
            return c != 0 ? c : x.CompareTo(y);
        });
        return idx.Select(i => items[i]).ToList();
    }

    /// <summary>Case-insensitive compare where digit runs compare as numbers ("tv2" before "tv10").</summary>
    public static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>"12 online · 1 new · 3 offline" (parts omitted when 0).</summary>
    public static string CountText(IReadOnlyCollection<NetDevice> all)
    {
        int on = all.Count(d => d.Online), nw = all.Count(d => d.IsNew), off = all.Count - on;
        return L("%lld online", on) + (nw > 0 ? L(" · %lld new", nw) : "") + (off > 0 ? L(" · %lld offline", off) : "");
    }

    /// <summary>The single centred line of an empty list, in the spec's priority order; null when there are rows.</summary>
    public static string? EmptyText(bool hasNetwork, bool autoScan, string search, bool scanning, NetFilter filter, string? rangeText)
    {
        if (!hasNetwork) return autoScan ? L("Not connected to a network") : L("Press Scan to look for devices");
        if (search.Trim().Length > 0) return L("Nothing matches “%@”", search);
        if (scanning) return L("Looking for devices…");
        if (filter != NetFilter.All) return L("No %@ devices", FilterTitle(filter).ToLowerInvariant());
        if (rangeText != null) return L("Nothing answered in %@", rangeText);
        return L("No devices found");
    }

    /// <summary>nil → "–", under 1 → "&lt;1", else the rounded integer.</summary>
    public static string MsText(double? ms) => ms is not { } m ? "–" : m < 1 ? "<1" : ((long)Math.Round(m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    /// <summary>"5 min ago" / "3 h ago" / "2 d ago".</summary>
    public static string AgoText(TimeSpan age)
    {
        double s = Math.Max(0, age.TotalSeconds);
        return s < 3600 ? L("%lld min ago", Math.Max(1, (int)(s / 60))) : s < 86_400 ? L("%lld h ago", (int)(s / 3600)) : L("%lld d ago", (int)(s / 86_400));
    }

    /// <summary>Segoe Fluent glyph of a device kind.</summary>
    public static string KindGlyph(DeviceKind k) => k switch
    {
        DeviceKind.Phone => "", DeviceKind.Computer => "", DeviceKind.Tv => "", DeviceKind.Speaker => "", DeviceKind.Printer => "",
        DeviceKind.Router => "", DeviceKind.Nas => "", DeviceKind.SmartHome => "", DeviceKind.Console => "", _ => "",
    };

    /// <summary>"Answers in 18 ms · no packet loss (3 pings)" and its variants.</summary>
    public static string LatencyText(NetDevice d)
    {
        if (d.Ms is not { } ms) return L("Doesn't answer (many phones and TVs sleep or ignore pings; that's normal)");
        if (d.ViaTcp) return L("Answers in %@ ms (via a TCP connection; it ignores pings)", MsText(ms));
        if (d.Loss is not { } loss) return L("Answers in %@ ms", MsText(ms));
        int lost = (int)Math.Round(loss * 3);
        return L("Answers in %@ ms · ", MsText(ms)) + (lost == 0 ? L("no packet loss (3 pings)") : L("%lld of 3 pings lost", lost));
    }

    /// <summary>Scan range popover: valid text → count line and the "outside your own network" note.</summary>
    public static string RangeHelp(string text, ScanInfo? info)
    {
        var t = text.Trim();
        if (t.Length == 0) return L("A block (192.168.1.0/24), a range (192.168.1.10-192.168.1.80 or 192.168.1.10-80) or one address. Up to %lld addresses.", NetAddress.MaxRange);
        var r = NetAddress.ParseRange(t);
        if (!r.Ok) return r.Message;
        var s = r.Count == 1 ? L("1 address") : L("%lld addresses", r.Count);
        if (info != null && (!info.IsLocal(r.First) || !info.IsLocal(r.Last)))
            s += L(". Outside your own network: Fusebox can find devices and their names, but not hardware addresses or makers (routers don't pass those on).");
        return s;
    }

    /// <summary>Recent ranges (newest first, max 5): the applied one moves to the top.</summary>
    public static List<string> PushRecent(IEnumerable<string> recent, string applied) =>
        new[] { applied }.Concat(recent.Where(r => !r.Equals(applied, StringComparison.OrdinalIgnoreCase))).Take(5).ToList();

    /// <summary>Expected scan time for the progress bar: 8 s for the own network, longer for big custom ranges.</summary>
    public static double ExpectedSeconds(bool custom, int hosts, int remote) => custom ? Math.Max(8, 4 + hosts * 0.003 + remote / 32.0 * 0.4) : 8;
}
