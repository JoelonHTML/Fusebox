using System.Globalization;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Network;

public enum RangeError { None, Invalid, Reversed, TooLarge }

/// <summary>Result of parsing the "Scan any IP range" box. Count is the number of addresses (also when too large).</summary>
public readonly record struct RangeResult(uint First, uint Last, RangeError Error, int Count)
{
    public bool Ok => Error == RangeError.None;

    public string Message => Error switch
    {
        RangeError.Invalid => L("Not a range. Try 192.168.1.0/24, 192.168.1.10-80 or a single address."),
        RangeError.Reversed => L("The first address is higher than the last one."),
        RangeError.TooLarge => L("That's %lld addresses; Fusebox scans at most %lld at a time (a /20). Use a smaller block.", Count, NetAddress.MaxRange),
        _ => "",
    };
}

/// <summary>IPv4/MAC parsing and range logic, ported from the Mac's NetScanner (same tests). IPs are host-order uints.</summary>
public static class NetAddress
{
    /// <summary>Windows cap: a /22 block (1024 addresses). The Mac allows 4096.</summary>
    public const int MaxRange = 1024;

    public static uint? ParseIp(string? s)
    {
        if (s == null) return null;
        var p = s.Split('.');
        if (p.Length != 4) return null;
        uint v = 0;
        foreach (var part in p)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) || int.Parse(part, CultureInfo.InvariantCulture) > 255) return null;
            v = v << 8 | uint.Parse(part, CultureInfo.InvariantCulture);
        }
        return v;
    }

    public static string IpString(uint ip) => $"{ip >> 24}.{ip >> 16 & 255}.{ip >> 8 & 255}.{ip & 255}";

    public static byte[]? ParseMac(string? s)
    {
        var p = s?.Split(':', '-');
        if (p is not { Length: 6 }) return null;
        var b = new byte[6];
        for (int i = 0; i < 6; i++)
            if (p[i].Length != 2 || !byte.TryParse(p[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i])) return null;
        return b;
    }

    public static string MacString(byte[] mac) => string.Join(":", mac.Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Locally administered address (bit 2 of the first byte): random MAC of phones/laptops with Wi-Fi privacy.</summary>
    public static bool IsPrivateMac(byte[] mac) => mac.Length > 0 && (mac[0] & 0x02) != 0;

    public static uint MaskFromPrefix(int bits) => bits <= 0 ? 0 : bits >= 32 ? uint.MaxValue : uint.MaxValue << (32 - bits);

    /// <summary>
    /// Addresses to scan in our own subnet: the whole subnet down to a /22; anything bigger → the /22 around our address.
    /// No network/broadcast address; /31 and /32 have no neighbours.
    /// </summary>
    public static List<uint> Hosts(uint ip, int prefix)
    {
        uint m = MaskFromPrefix(Math.Max(prefix, 22));
        uint net = ip & m, broadcast = net | ~m;
        var l = new List<uint>();
        if (broadcast <= net + 1) return l;
        for (uint a = net + 1; a < broadcast; a++) l.Add(a);
        return l;
    }

    /// <summary>"192.168.1.0/24", "192.168.1.10-192.168.1.80", "192.168.1.10-80" or one address. Spaces allowed.</summary>
    public static RangeResult ParseRange(string text)
    {
        var s = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        uint a, b;
        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            if (ParseIp(s[..slash]) is not { } ip || !int.TryParse(s[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int bits) || bits > 32)
                return new(0, 0, RangeError.Invalid, 0);
            uint mask = MaskFromPrefix(bits), net = ip & mask, last = net | ~mask;
            (a, b) = bits >= 31 ? (net, last) : (net + 1, last - 1);
        }
        else
        {
            var parts = s.Split('-');
            if (parts.Length > 2 || ParseIp(parts[0]) is not { } first) return new(0, 0, RangeError.Invalid, 0);
            a = b = first;
            if (parts.Length == 2)
            {
                if (ParseIp(parts[1]) is { } second) b = second;
                else if (parts[1].Length is > 0 and <= 3 && parts[1].All(char.IsAsciiDigit) && uint.Parse(parts[1], CultureInfo.InvariantCulture) < 256)
                    b = a & 0xFFFFFF00 | uint.Parse(parts[1], CultureInfo.InvariantCulture);
                else return new(0, 0, RangeError.Invalid, 0);
            }
            if (a > b) return new(a, b, RangeError.Reversed, 0);
        }
        long count = (long)b - a + 1;
        return count > MaxRange ? new(a, b, RangeError.TooLarge, (int)Math.Min(count, int.MaxValue)) : new(a, b, RangeError.None, (int)count);
    }

    /// <summary>Private (RFC 1918 / link-local) address.</summary>
    public static bool IsLan(uint ip) => ip >> 24 == 10 || ip >> 20 == 0xAC1 || ip >> 16 == 0xC0A8 || ip >> 16 == 0xA9FE;
}
