using System.Globalization;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Docked;

/// <summary>
/// One USB device; the tree is flattened depth-first, Depth = indentation. Bps/MaxBps = link now / capable of (bit/s, 0 = unknown).
/// Path = port numbers from the root hub ("0/3/2"), never an instance id (those can contain serial numbers).
/// Connectable = the root port is a user-reachable connector (USB_PORT_PROPERTIES), null = unknown.
/// Raw = a few plain device properties for "All values"; run through <see cref="RawFilter"/> before showing.
/// </summary>
public sealed record UsbNode(string Name, string? Maker, int Depth, bool IsHub, string? Speed, string? Max, bool Slow, bool? TypeC,
    long Bps = 0, long MaxBps = 0, int? Vid = null, int? Pid = null, int Class = -1, int? Ma = null, bool? Connectable = null,
    string Path = "", int Root = 0, IReadOnlyList<(string Key, string Value)>? Raw = null);
/// <summary>Tech = DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY. Native/MaxHz from the mode list, Scale = DPI scaling in percent, Bits = bits per channel.</summary>
public sealed record DisplayInfo(string Name, string Connection, int Width, int Height, double Hz, bool? Hdr,
    uint Tech = 0, int NativeW = 0, int NativeH = 0, double MaxHz = 0, int? Scale = null, int? Bits = null);
/// <summary>Kind = <see cref="BtKind"/> id ("" unknown).</summary>
public sealed record BtDevice(string Name, bool Connected, int? Battery, string Kind = "");
public sealed record AudioEndpoint(string Name, string Kind, bool IsOutput, bool IsDefault, bool Plugged);
/// <summary>Watts = charge rate in W while charging (battery IOCTL), Minutes = time left on battery or to full while charging.</summary>
public sealed record PowerInfo(bool? OnAc, int? Percent, bool Charging, bool HasBattery, double? Watts = null, int? Minutes = null);
/// <summary>Internet through a phone, modem or metered link; Kind = <see cref="Uplink"/> id. Interface = adapter description (never the network name).</summary>
public sealed record UplinkInfo(string Kind, string? Interface, bool Metered);
/// <summary>Bluetooth is null when the PC has no Bluetooth radio (section hidden).</summary>
public sealed record DockedSnapshot(PowerInfo Power, IReadOnlyList<UsbNode> Usb, IReadOnlyList<DisplayInfo> Displays, IReadOnlyList<BtDevice>? Bluetooth, IReadOnlyList<AudioEndpoint> Audio, UplinkInfo? Uplink = null);

/// <summary>Link speed now vs. maximum, from usbioctl.h values (the Mac's "link speed now vs. maximum").</summary>
public static class UsbSpeed
{
    public const uint FlagRunsSuper = 1, FlagCapableSuper = 2, FlagRunsSuperPlus = 4, FlagCapableSuperPlus = 8;
    public const uint ProtoUsb300 = 4;

    /// <summary>speed = USB_DEVICE_SPEED (0 low, 1 full, 2 high, 3 super); flags = EX_V2 Flags; protocols = SupportedUsbProtocols.</summary>
    public static (string Now, string Max, bool Slow) Describe(int speed, uint flags, uint protocols)
    {
        var (now, max) = Ranks(speed, flags, protocols);
        return (Name(now), Name(max), max > now);
    }

    /// <summary>Rank now / capable: 0 low, 1 full, 2 high, 3 SuperSpeed, 4 SuperSpeed+.</summary>
    public static (int Now, int Max) Ranks(int speed, uint flags, uint protocols)
    {
        int now = speed switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, _ => 2 };
        if ((flags & FlagRunsSuperPlus) != 0) now = 4;
        int max = now;
        if ((flags & FlagCapableSuper) != 0 || (protocols & ProtoUsb300) != 0) max = Math.Max(max, 3);
        if ((flags & FlagCapableSuperPlus) != 0) max = 4;
        return (now, max);
    }

    /// <summary>Signalling rate of a rank in bit/s.</summary>
    public static long Bps(int rank) => rank switch { 0 => 1_500_000, 1 => 12_000_000, 2 => 480_000_000, 3 => 5_000_000_000, _ => 10_000_000_000 };

    /// <summary>"USB 1" / "USB 2" / "USB 3" from a link rate (Mac: &lt; 100 Mbit/s, &lt; 1 Gbit/s, else).</summary>
    public static string Family(long bps) => bps < 100_000_000 ? "USB 1" : bps < 1_000_000_000 ? "USB 2" : "USB 3";

    /// <summary>bMaxPower of the configuration descriptor in mA: 2 mA units up to high speed, 8 mA units for SuperSpeed. 0 = unknown.</summary>
    public static int? PowerMa(int bMaxPower, int speed) => bMaxPower <= 0 ? null : bMaxPower * (speed >= 3 ? 8 : 2);

    public static string Name(int rank) => rank switch
    {
        0 => L("USB 1 (1.5 Mb/s)"),
        1 => L("USB 1 (12 Mb/s)"),
        2 => L("USB 2 (480 Mb/s)"),
        3 => L("USB 3.2 Gen 1 (5 Gb/s)"),
        _ => L("USB 3.2 Gen 2 (10 Gb/s)"),
    };
}

public static class DisplayMap
{
    /// <summary>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY to plain text.</summary>
    public static string Connection(uint tech) => tech switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        6 or 11 or 13 or 0x80000000 => L("Built-in"),
        10 or 12 => "DisplayPort",
        18 => L("USB-C (DP alt mode)"),
        15 => L("Wireless (Miracast)"),
        16 => L("DisplayLink or USB graphics"),
        17 => L("Virtual"),
        _ => L("Display"),
    };

    public static bool IsBuiltIn(uint tech) => tech is 6 or 11 or 13 or 0x80000000;
    /// <summary>Indirect displays (DisplayLink and other USB graphics): tech 16/17.</summary>
    public static bool IsIndirect(uint tech) => tech is 16 or 17;

    public static double Hz(uint num, uint den) => den == 0 ? 0 : Math.Round((double)num / den, 2);

    /// <summary>"60 Hz", "59.94 Hz".</summary>
    public static string HzText(double hz) =>
        Math.Abs(hz - Math.Round(hz)) < 0.05 ? $"{Math.Round(hz):0} Hz" : hz.ToString("0.##", Culture) + " Hz";
}

public static class BtBattery
{
    /// <summary>PnP property {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2: a byte 0..100; anything else = unknown.</summary>
    public static int? Parse(byte[]? raw) => raw is { Length: >= 1 } && raw[0] <= 100 ? raw[0] : null;
}

public static class AudioMap
{
    /// <summary>EndpointFormFactor (mmdeviceapi.h) to plain text.</summary>
    public static string FormFactor(uint f) => f switch
    {
        1 => L("Speakers"),
        2 => L("Line level"),
        3 => L("Headphones"),
        4 => L("Microphone"),
        5 => L("Headset"),
        6 => L("Handset"),
        8 => "S/PDIF",
        9 => L("Display audio (HDMI or DisplayPort)"),
        _ => L("Audio"),
    };
}

public static class PowerMap
{
    /// <summary>SYSTEM_POWER_STATUS bytes: acLine 0/1/255, batteryFlag (8 charging, 128 none, 255 unknown), percent 0..100 or 255.</summary>
    public static PowerInfo From(byte acLine, byte batteryFlag, byte percent)
    {
        bool has = batteryFlag != 128 && batteryFlag != 255;
        return new(acLine == 255 ? null : acLine == 1, has && percent <= 100 ? percent : null, (batteryFlag & 8) != 0, has);
    }
}

/// <summary>Keys never shown in "All values": anything that can identify one unit or one machine.</summary>
public static class RawFilter
{
    static readonly string[] Banned = ["serial", "address", "uuid", "guid", "instance", "container"];
    public static bool Keep(string key) => !Banned.Any(b => key.Contains(b, StringComparison.OrdinalIgnoreCase));
    public static IReadOnlyList<(string Key, string Value)> Clean(IReadOnlyList<(string Key, string Value)>? raw) =>
        raw == null ? [] : raw.Where(r => Keep(r.Key) && r.Value.Length > 0).Select(r => (r.Key, r.Value.Length > 300 ? r.Value[..300] : r.Value)).ToList();
}

/// <summary>Bluetooth class-of-device to a device kind id (keyboard, mouse, headphones, earbuds, speaker, controller, phone, watch, computer, tablet, pen, "" = unknown).</summary>
public static class BtKind
{
    public static string From(uint? cod, string name)
    {
        if (cod is uint c)
        {
            uint major = c >> 8 & 0x1F, minor = c >> 2 & 0x3F;
            switch (major)
            {
                case 1: return "computer";
                case 2: return "phone";
                case 4:
                    return minor switch { 1 or 2 => "headphones", 6 => "headphones", 5 => "speaker", 4 => "", _ => "" } is { Length: > 0 } k ? k : ByName(name);
                case 5:
                    uint dev = minor >> 4;   // keyboard 1, pointing 2, combo 3
                    if (dev == 1 || dev == 3) return "keyboard";
                    if (dev == 2) return "mouse";
                    return (minor & 0xF) is 1 or 2 ? "controller" : ByName(name);
                case 7: return minor == 1 ? "watch" : ByName(name);
            }
        }
        return ByName(name);
    }

    static string ByName(string name)
    {
        var n = name.ToLowerInvariant();
        string[][] map =
        [
            ["keyboard", "keyboard"], ["mouse", "mouse"], ["trackpad", "mouse"], ["airpods", "earbuds"], ["buds", "earbuds"],
            ["headphone", "headphones"], ["headset", "headphones"], ["speaker", "speaker"], ["soundbar", "speaker"],
            ["controller", "controller"], ["gamepad", "controller"], ["watch", "watch"], ["iphone", "phone"], ["phone", "phone"],
            ["ipad", "tablet"], ["pen", "pen"], ["pencil", "pen"],
        ];
        foreach (var m in map) if (n.Contains(m[0])) return m[1];
        return "";
    }

    public static string Title(string kind) => kind switch
    {
        "keyboard" => L("Keyboard"), "mouse" => L("Mouse"), "headphones" => L("Headphones"), "earbuds" => L("Earbuds"),
        "speaker" => L("Speaker"), "controller" => L("Controller"), "phone" => L("Phone"), "watch" => L("Watch"),
        "computer" => L("Computer"), "tablet" => L("Tablet"), "pen" => L("Pen"), _ => L("Device"),
    };

    public static string Glyph(string kind) => kind switch
    {
        "keyboard" => "\uE765", "mouse" => "\uE962", "headphones" or "earbuds" => "\uE7F6", "speaker" => "\uE7F5",
        "controller" => "\uE7FC", "phone" or "tablet" => "\uE8EA", "watch" => "\uE916", "computer" => "\uE977", _ => "\uE702",
    };
}

/// <summary>Internet through a phone, modem or metered link (Mac "Uplink" rules, Windows inputs from ConnectionProfile).</summary>
public static class Uplink
{
    /// <summary>wwan = IsWwanConnectionProfile, iana = adapter IanaInterfaceType (71 Wi-Fi, 243/244 mobile), desc = adapter description, metered = cost is not unrestricted. Null = nothing to show.</summary>
    public static string? Classify(bool wwan, int iana, string desc, bool metered)
    {
        var d = desc.ToLowerInvariant();
        if (d.Contains("iphone") || d.Contains("apple mobile")) return "iphone";
        if (d.Contains("ipad")) return "ipad";
        if (d.Contains("bluetooth")) return "bluetooth";
        if (wwan || iana is 243 or 244 || System.Text.RegularExpressions.Regex.IsMatch(d, @"\b(modem|lte|4g|5g|cellular|mobile broadband)\b")) return "modem";
        if (iana == 71 && metered) return "hotspot";
        return metered ? "metered" : null;
    }

    public static string Title(string kind) => kind switch
    {
        "iphone" => L("Internet via %@ (USB)", "iPhone"),
        "ipad" => L("Internet via %@ (USB)", "iPad"),
        "bluetooth" => L("Internet via Bluetooth tethering"),
        "modem" => L("Internet via mobile modem"),
        "hotspot" => L("Internet via phone hotspot"),
        _ => L("Internet via a metered connection"),
    };

    public static string Via(string kind) => kind switch { "iphone" or "ipad" => "USB", "bluetooth" => "Bluetooth", "modem" => L("modem"), "hotspot" => "Wi-Fi", _ => L("metered") };
    public static string Glyph(string kind) => kind == "modem" ? "\uE702" : "\uE701";
}

/// <summary>Headphone jack from the audio endpoints: an unplugged headphone endpoint = an empty jack, an active one that shares its sound card with speakers = plugged.</summary>
public sealed record JackInfo(bool Present, bool Plugged, bool Headset, bool InUse);

public static class JackMap
{
    /// <summary>"Headphones (Realtek(R) Audio)" gives "Realtek(R) Audio".</summary>
    public static string Adapter(string name)
    {
        int i = name.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 && name.EndsWith(')') ? name[(i + 2)..^1] : "";
    }

    public static JackInfo Find(IReadOnlyList<AudioEndpoint> audio)
    {
        bool Phones(AudioEndpoint a) => a.IsOutput && (a.Kind == L("Headphones") || a.Kind == L("Headset"));
        var empty = audio.FirstOrDefault(a => Phones(a) && !a.Plugged);
        var speakerCards = audio.Where(a => a.IsOutput && a.Kind == L("Speakers")).Select(a => Adapter(a.Name)).Where(x => x.Length > 0).ToHashSet();
        var plugged = audio.FirstOrDefault(a => Phones(a) && a.Plugged && speakerCards.Contains(Adapter(a.Name)));
        if (plugged != null) return new JackInfo(true, true, plugged.Kind == L("Headset"), plugged.IsDefault);
        return new JackInfo(empty != null, false, false, false);
    }
}
