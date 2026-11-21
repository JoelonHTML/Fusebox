using System.Text;
using System.Xml;

namespace Fusebox.Core.Network;

/// <summary>
/// Raw packets of the LAN scan, no sockets: mDNS/DNS queries and answers, NetBIOS node status, SSDP, UPnP descriptions, Wake on LAN.
/// Replies come from the network and are untrusted: every reader checks sizes and counts.
/// </summary>
public static class NetPackets
{
    public static readonly uint MdnsGroup = 0xE00000FB;     // 224.0.0.251
    public static readonly uint SsdpGroup = 0xEFFFFFFA;     // 239.255.255.250

    // ---- DNS ----

    /// <summary>"x.x.x.x.in-addr.arpa" for an address.</summary>
    public static string ReverseName(uint ip) => $"{ip & 255}.{ip >> 8 & 255}.{ip >> 16 & 255}.{ip >> 24}.in-addr.arpa";

    /// <summary>One DNS question; <paramref name="unicast"/> sets the mDNS QU bit (the answer comes straight to us).</summary>
    public static byte[] DnsQuery(string name, ushort type, ushort id = 0, bool unicast = true)
    {
        var p = new List<byte> { (byte)(id >> 8), (byte)id, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var b = Encoding.UTF8.GetBytes(label);
            int n = Math.Min(b.Length, 63);
            p.Add((byte)n); p.AddRange(b.Take(n));
        }
        p.Add(0);
        p.AddRange([(byte)(type >> 8), (byte)type, (byte)(unicast ? 0x80 : 0), 1]);
        return p.ToArray();
    }

    /// <summary>Reads a DNS name with compression pointers (≤ 16 jumps); <paramref name="off"/> ends behind the name.</summary>
    public static string? ReadName(byte[] b, ref int off)
    {
        var labels = new List<string>();
        int i = off, hops = 0;
        bool jumped = false;
        while (i < b.Length)
        {
            int len = b[i];
            if (len == 0) { if (!jumped) off = i + 1; return string.Join('.', labels); }
            if ((len & 0xC0) == 0xC0)
            {
                if (i + 1 >= b.Length || hops >= 16) return null;
                if (!jumped) off = i + 2;
                jumped = true; hops++;
                i = (len & 0x3F) << 8 | b[i + 1];
                continue;
            }
            if (len >= 64 || i + 1 + len > b.Length || labels.Count >= 64) return null;
            labels.Add(Encoding.UTF8.GetString(b, i + 1, len));
            i += 1 + len;
        }
        return null;
    }

    readonly record struct Rr(string Owner, int Type, int Data, int Len);

    /// <summary>Answer, authority and additional records of a response (question section skipped), at most <paramref name="max"/>.</summary>
    static List<Rr> Records(byte[] b, int max)
    {
        var res = new List<Rr>();
        if (b.Length < 12 || b.Length > 9000) return res;
        int qd = b[4] << 8 | b[5], rr = (b[6] << 8 | b[7]) + (b[8] << 8 | b[9]) + (b[10] << 8 | b[11]);
        int off = 12;
        for (int i = 0; i < Math.Min(qd, 16); i++) { if (ReadName(b, ref off) == null) return res; off += 4; }
        for (int i = 0; i < Math.Min(rr, max); i++)
        {
            if (ReadName(b, ref off) is not { } owner || off + 10 > b.Length) break;
            int type = b[off] << 8 | b[off + 1], len = b[off + 8] << 8 | b[off + 9], start = off + 10;
            if (start + len > b.Length) break;
            off = start + len;
            res.Add(new(owner, type, start, len));
        }
        return res;
    }

    /// <summary>PTR answers for "x.x.x.x.in-addr.arpa" → ip → hostname (shortened, no ".local").</summary>
    public static Dictionary<uint, string> ReverseAnswers(byte[] b)
    {
        var res = new Dictionary<uint, string>();
        const string suffix = ".in-addr.arpa";
        foreach (var r in Records(b, 64))
        {
            var lower = r.Owner.ToLowerInvariant();
            if (r.Type != 12 || !lower.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var parts = lower[..^suffix.Length].Split('.');
            if (parts.Length != 4 || NetAddress.ParseIp(string.Join('.', parts.Reverse())) is not { } ip) continue;
            int o = r.Data;
            if (ReadName(b, ref o) is { } host && Devices.CleanHostname(host) is { } name) res[ip] = NetNames.Cap(name);
        }
        return res;
    }

    /// <summary>mDNS answer to a "_type._tcp.local" PTR question: (service type, instance name, TXT pairs). TXT usually rides along as an additional record.</summary>
    public static List<(string Type, string Instance, Dictionary<string, string> Txt)> MdnsServices(byte[] b)
    {
        var recs = Records(b, 128);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in recs.Where(r => r.Type == 16))
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int o = r.Data, end = r.Data + r.Len;
            while (o < end && d.Count < 24)
            {
                int n = b[o++];
                if (o + n > end) break;
                var s = Encoding.UTF8.GetString(b, o, Math.Min(n, 100));
                o += n;
                int eq = s.IndexOf('=');
                if (eq > 0) d[s[..eq]] = s[(eq + 1)..];
            }
            txt[r.Owner] = d;
        }
        var res = new List<(string, string, Dictionary<string, string>)>();
        foreach (var r in recs.Where(r => r.Type == 12))
        {
            var type = NetNames.ServiceTypes.FirstOrDefault(t => r.Owner.Equals(t + ".local", StringComparison.OrdinalIgnoreCase));
            if (type == null) continue;
            int o = r.Data;
            if (ReadName(b, ref o) is not { } full) continue;
            var tail = "." + type + ".local";
            if (!full.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) continue;
            res.Add((type, full[..^tail.Length], txt.TryGetValue(full, out var t) ? t : []));
        }
        return res;
    }

    // ---- NetBIOS (Windows, NAS, printers: computer name over UDP 137) ----

    public static byte[] NetbiosQuery()
    {
        var p = new List<byte> { 0x46, 0x42, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x20 };
        foreach (byte c in new byte[] { 0x2A }.Concat(new byte[15])) { p.Add((byte)(0x41 + (c >> 4))); p.Add((byte)(0x41 + (c & 0x0F))); }
        p.AddRange([0, 0, 0x21, 0, 1]);   // NBSTAT, IN
        return p.ToArray();
    }

    /// <summary>Node-status answer → first unique name with suffix 0x00 (the computer name); ASCII only.</summary>
    public static string? NetbiosName(byte[] b)
    {
        if (b.Length < 12 || b.Length > 2048) return null;
        int off = 12;
        if (ReadName(b, ref off) == null || off + 11 > b.Length || b[off] != 0 || b[off + 1] != 0x21) return null;
        int count = b[off + 10], o = off + 11;
        for (int i = 0; i < count; i++)
        {
            if (o + 18 > b.Length) break;
            bool group = (b[o + 16] & 0x80) != 0;
            if (b[o + 15] == 0 && !group)
            {
                var name = new string(b.Skip(o).Take(15).Select(x => (char)x).ToArray()).Trim(' ', '\0');
                if (name.Length > 0 && name.All(c => c is >= ' ' and < (char)127)) return name;
            }
            o += 18;
        }
        return null;
    }

    // ---- SSDP / UPnP ----

    public static readonly byte[] SsdpSearch = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");

    /// <summary>SSDP reply → header lines (keys lower case). Empty when it isn't a valid reply.</summary>
    public static Dictionary<string, string> SsdpHeaders(byte[] b)
    {
        var h = new Dictionary<string, string>();
        if (b.Length > 4096) return h;
        var lines = Encoding.UTF8.GetString(b).Split('\n').Select(l => l.Trim()).ToArray();
        if (lines.Length == 0 || !(lines[0].StartsWith("HTTP/1.1 200", StringComparison.Ordinal) || lines[0].StartsWith("NOTIFY", StringComparison.Ordinal))) return h;
        foreach (var line in lines.Skip(1).Take(40))
        {
            int c = line.IndexOf(':');
            if (c > 0) h[line[..c].Trim().ToLowerInvariant()] = line[(c + 1)..].Trim();
        }
        return h;
    }

    /// <summary>LOCATION is only followed when it is http and points to the same private LAN address as the sender.</summary>
    public static Uri? LanUrl(string s, uint from)
    {
        if (s.Length >= 300 || !Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != "http" || NetAddress.ParseIp(u.Host) is not { } ip) return null;
        return ip == from && NetAddress.IsLan(ip) ? u : null;
    }

    /// <summary>UPnP device description → name, maker, model, type (first occurrence = the root device). No DTD/entities (no XML bombs), ≤ 64 KB, no UTF-16.</summary>
    public static UpnpInfo? ParseUpnp(byte[] data)
    {
        if (data.Length > 65_536 || (data.Length > 1 && ((data[0] == 0xFE && data[1] == 0xFF) || (data[0] == 0xFF && data[1] == 0xFE)))) return null;
        var text = Encoding.UTF8.GetString(data);
        if (text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase)) return null;
        string? fn = null, mf = null, mn = null, dt = null;
        try
        {
            using var r = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65_536 });
            bool more = r.Read();
            while (more)
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName is not ("friendlyName" or "manufacturer" or "modelName" or "deviceType")) { more = r.Read(); continue; }
                var name = r.LocalName;
                var v = r.ReadElementContentAsString().Trim();   // already moves to the next node: don't Read() again
                more = !r.EOF;
                if (v.Length is 0 or > 100) continue;
                switch (name)
                {
                    case "friendlyName": fn ??= v; break;
                    case "manufacturer": mf ??= v; break;
                    case "modelName": mn ??= v; break;
                    default: dt ??= v; break;
                }
            }
        }
        catch (XmlException) { }
        return fn == null && mf == null && mn == null && dt == null ? null : new(fn, mf, mn, dt);
    }

    // ---- Wake on LAN ----

    /// <summary>6 × FF + 16 × MAC = 102 bytes.</summary>
    public static byte[]? MagicPacket(byte[] mac)
    {
        if (mac.Length != 6) return null;
        var p = new byte[102];
        Array.Fill(p, (byte)0xFF, 0, 6);
        for (int i = 0; i < 16; i++) mac.CopyTo(p, 6 + i * 6);
        return p;
    }
}
