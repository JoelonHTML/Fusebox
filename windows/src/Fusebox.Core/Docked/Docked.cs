using System.Globalization;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Docked;

/// <summary>One USB device; the tree is flattened depth-first, Depth = indentation.</summary>
public sealed record UsbNode(string Name, string? Maker, int Depth, bool IsHub, string? Speed, string? Max, bool Slow, bool? TypeC);
public sealed record DisplayInfo(string Name, string Connection, int Width, int Height, double Hz, bool? Hdr);
public sealed record BtDevice(string Name, bool Connected, int? Battery);
public sealed record AudioEndpoint(string Name, string Kind, bool IsOutput, bool IsDefault, bool Plugged);
public sealed record PowerInfo(bool? OnAc, int? Percent, bool Charging, bool HasBattery);
/// <summary>Bluetooth is null when the PC has no Bluetooth radio (section hidden).</summary>
public sealed record DockedSnapshot(PowerInfo Power, IReadOnlyList<UsbNode> Usb, IReadOnlyList<DisplayInfo> Displays, IReadOnlyList<BtDevice>? Bluetooth, IReadOnlyList<AudioEndpoint> Audio);

/// <summary>Link speed now vs. maximum, from usbioctl.h values (the Mac's "link speed now vs. maximum").</summary>
public static class UsbSpeed
{
    public const uint FlagRunsSuper = 1, FlagCapableSuper = 2, FlagRunsSuperPlus = 4, FlagCapableSuperPlus = 8;
    public const uint ProtoUsb300 = 4;

    /// <summary>speed = USB_DEVICE_SPEED (0 low, 1 full, 2 high, 3 super); flags = EX_V2 Flags; protocols = SupportedUsbProtocols.</summary>
    public static (string Now, string Max, bool Slow) Describe(int speed, uint flags, uint protocols)
    {
        int now = speed switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, _ => 2 };
        if ((flags & FlagRunsSuperPlus) != 0) now = 4;
        int max = now;
        if ((flags & FlagCapableSuper) != 0 || (protocols & ProtoUsb300) != 0) max = Math.Max(max, 3);
        if ((flags & FlagCapableSuperPlus) != 0) max = 4;
        return (Name(now), Name(max), max > now);
    }

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
        18 => L("DisplayPort over USB-C"),
        15 => L("Wireless (Miracast)"),
        16 => L("USB or DisplayLink"),
        17 => L("Virtual"),
        _ => L("Display"),
    };

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
