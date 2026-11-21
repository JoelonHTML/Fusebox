using System.Runtime.InteropServices;
using System.Text;
using Fusebox.Core.Network;

namespace Fusebox.Platform.Network;

/// <summary>Current Wi-Fi link. Ssid/Bssid are null when Windows withholds them (24H2+ needs location permission): then NameHidden is true.</summary>
public sealed record WifiInfo(bool Connected, string? Ssid, string? Bssid, int? SignalPercent, int? Rssi, int? Channel, string? Band, string? Phy, int? RxMbps, int? TxMbps, bool NameHidden, int? WidthMhz = null);

/// <summary>wlanapi.dll: WlanQueryInterface(current_connection) + WlanGetNetworkBssList. Offsets follow wlanapi.h (x64/arm64 layout).</summary>
public static unsafe partial class Wifi
{
    [LibraryImport("wlanapi.dll")] private static partial uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiated, out nint handle);
    [LibraryImport("wlanapi.dll")] private static partial uint WlanCloseHandle(nint handle, nint reserved);
    [LibraryImport("wlanapi.dll")] private static partial uint WlanEnumInterfaces(nint handle, nint reserved, out nint list);
    [LibraryImport("wlanapi.dll")] private static partial uint WlanQueryInterface(nint handle, Guid* guid, int opcode, nint reserved, out uint size, out nint data, nint opType);
    [LibraryImport("wlanapi.dll")] private static partial uint WlanGetNetworkBssList(nint handle, Guid* guid, nint ssid, int bssType, int securityEnabled, nint reserved, out nint list);
    [LibraryImport("wlanapi.dll")] private static partial void WlanFreeMemory(nint p);

    const int OpRssi = 16, OpCurrentConnection = 7, StateConnected = 1;

    /// <summary>Null = no Wi-Fi adapter (or no WLAN service): hide the section.</summary>
    public static WifiInfo? Read()
    {
        nint h = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out h) != 0) return null;
            if (WlanEnumInterfaces(h, 0, out var list) != 0 || list == 0) return null;
            Guid guid; int state;
            try
            {
                var p = (byte*)list;
                uint n = *(uint*)p;
                if (n == 0) return null;
                // WLAN_INTERFACE_INFO (532 bytes) at offset 8: GUID, WCHAR[256] description, state. Take the first connected one, else the first.
                int pick = 0;
                for (int i = 0; i < n; i++) if (*(int*)(p + 8 + i * 532 + 16 + 512) == StateConnected) { pick = i; break; }
                guid = *(Guid*)(p + 8 + pick * 532);
                state = *(int*)(p + 8 + pick * 532 + 16 + 512);
            }
            finally { WlanFreeMemory(list); }

            if (state != StateConnected) return new WifiInfo(false, null, null, null, null, null, null, null, null, null, false);

            string? ssid = null, bssid = null, phy = null;
            int? quality = null, rx = null, tx = null, rssi = null, channel = null;
            string? band = null;
            int? width = null;
            byte[]? bssidBytes = null;
            if (WlanQueryInterface(h, &guid, OpCurrentConnection, 0, out _, out var data, 0) == 0 && data != 0)
            {
                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES: state(4) mode(4) profile WCHAR[256] → association attributes at 520
                    var a = (byte*)data + 520;
                    uint len = *(uint*)a;
                    if (len is > 0 and <= 32) ssid = Encoding.UTF8.GetString(a + 4, (int)len);
                    bssidBytes = new ReadOnlySpan<byte>(a + 40, 6).ToArray();
                    if (bssidBytes.Any(b => b != 0)) bssid = NetAddress.MacString(bssidBytes); else bssidBytes = null;
                    phy = WifiMath.Phy(*(uint*)(a + 48));
                    quality = (int)*(uint*)(a + 56);
                    rx = (int)(*(uint*)(a + 60) / 1000);
                    tx = (int)(*(uint*)(a + 64) / 1000);
                }
                finally { WlanFreeMemory(data); }
            }

            // dBm + channel from the BSS list (the entry whose BSSID is ours)
            if (WlanGetNetworkBssList(h, &guid, 0, 3, 0, 0, out var bl) == 0 && bl != 0)
            {
                try
                {
                    var p = (byte*)bl;
                    uint count = *(uint*)(p + 4);
                    for (uint i = 0; i < count; i++)
                    {
                        var e = p + 8 + i * 360;
                        if (bssidBytes != null && !new ReadOnlySpan<byte>(e + 40, 6).SequenceEqual(bssidBytes)) continue;
                        rssi = *(int*)(e + 56);
                        uint khz = *(uint*)(e + 92);
                        band = WifiMath.Band(khz); channel = WifiMath.Channel(khz);
                        uint ieOff = *(uint*)(e + 352), ieLen = *(uint*)(e + 356);
                        if (ieLen is > 0 and < 4096 && ieOff + ieLen < 8192) width = WifiWidth.FromIes(new ReadOnlySpan<byte>(e + ieOff, (int)ieLen));
                        break;
                    }
                    if (bssidBytes == null) { rssi = null; band = null; channel = null; width = null; }   // without our BSSID we cannot tell which entry is ours
                }
                finally { WlanFreeMemory(bl); }
            }
            if (rssi == null && WlanQueryInterface(h, &guid, OpRssi, 0, out _, out var rd, 0) == 0 && rd != 0)
            {
                try { rssi = *(int*)rd; } finally { WlanFreeMemory(rd); }
            }
            if (rssi == null && quality != null) rssi = WifiMath.QualityToRssi(quality.Value);
            if (quality == null && rssi != null) quality = Math.Clamp((rssi.Value + 100) * 2, 0, 100);

            return new WifiInfo(true, ssid, bssid, quality, rssi, channel, band, phy, rx, tx, string.IsNullOrEmpty(ssid), width);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { if (h != 0) WlanCloseHandle(h, 0); }
    }
}
