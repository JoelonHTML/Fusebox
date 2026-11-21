using Fusebox.Core.Docked;
using static Fusebox.Platform.Docked.DockedNative;

namespace Fusebox.Platform.Docked;

/// <summary>Active displays via QueryDisplayConfig. Structs are read at fixed offsets (sizes checked by the probe).</summary>
internal static unsafe class Displays
{
    const int PathSize = 72, ModeSize = 64;   // DISPLAYCONFIG_PATH_INFO, DISPLAYCONFIG_MODE_INFO

    public static List<DisplayInfo> Read()
    {
        var res = new List<DisplayInfo>();
        uint np = 0, nm = 0;
        byte[]? paths = null, modes = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(2 /*QDC_ONLY_ACTIVE_PATHS*/, &np, &nm) != 0) return res;
            paths = new byte[np * PathSize + 8]; modes = new byte[nm * ModeSize + 8];
            int rc;
            fixed (byte* pp = paths) fixed (byte* pm = modes) rc = QueryDisplayConfig(2, &np, pp, &nm, pm, IntPtr.Zero);
            if (rc == 0) break;
            if (rc != 122 /*ERROR_INSUFFICIENT_BUFFER*/ || attempt == 2) return res;
        }
        fixed (byte* pp = paths) fixed (byte* pm = modes)
        {
            for (uint i = 0; i < np; i++)
            {
                byte* path = pp + i * PathSize;
                // path: source{luid 8, id 4, modeIdx 4, status 4} @0; target{luid 8 @20, id 4 @28, modeIdx @32, tech u32 @36, rotation @40, scaling @44, refresh num @48 den @52, scanline @56, available @60, status @64}, flags @68
                uint srcId = *(uint*)(path + 8), tgtId = *(uint*)(path + 28), tech = *(uint*)(path + 36);
                uint num = *(uint*)(path + 48), den = *(uint*)(path + 52);
                int w = 0, h = 0, px = 0, py = 0;
                for (uint j = 0; j < nm; j++)
                {
                    byte* mode = pm + j * ModeSize;   // infoType @0, id @4, luid @8, union @16
                    uint type = *(uint*)mode;
                    if (type == 1 && *(uint*)(mode + 4) == srcId && *(long*)(mode + 8) == *(long*)path) { w = *(int*)(mode + 16); h = *(int*)(mode + 20); px = *(int*)(mode + 28); py = *(int*)(mode + 32); }
                    // target mode: vSyncFreq rational @16+24, used when the path has no refresh rate
                    if (type == 2 && num == 0 && *(uint*)(mode + 4) == tgtId && *(long*)(mode + 8) == *(long*)(path + 20))
                    { num = *(uint*)(mode + 40); den = *(uint*)(mode + 44); }
                }
                var name = TargetName(path + 20, tgtId);
                var (nw, nh, maxHz) = Modes(SourceName(path, srcId), name);
                var (hdr, bits) = Color(path + 20, tgtId);
                res.Add(new DisplayInfo(name, DisplayMap.Connection(tech), w, h, DisplayMap.Hz(num, den), hdr, tech, nw, nh, maxHz, Scale(px, py), bits));
            }
        }
        return res;
    }

    // DISPLAYCONFIG_DEVICE_INFO_HEADER: type u32 @0, size u32 @4, adapter luid 8 @8, id u32 @16
    static void Header(byte* b, uint type, uint size, byte* luid, uint id)
    {
        new Span<byte>(b, (int)size).Clear();
        *(uint*)b = type; *(uint*)(b + 4) = size; *(long*)(b + 8) = *(long*)luid; *(uint*)(b + 16) = id;
    }

    static string TargetName(byte* luid, uint id)
    {
        // DISPLAYCONFIG_TARGET_DEVICE_NAME (420 bytes): header 20, flags @20, tech @24, edid ids @28, connector @32, friendly name WCHAR[64] @36
        byte* b = stackalloc byte[420];
        Header(b, 2, 420, luid, id);
        if (DisplayConfigGetDeviceInfo(b) != 0) return "Display";
        var s = new string((char*)(b + 36));
        return s.Length > 0 ? s : "Display";
    }

    /// <summary>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO (32 bytes): header 20, value @20 (bit0 supported, bit1 enabled), color encoding @24, bits per channel @28.</summary>
    static (bool? Hdr, int? Bits) Color(byte* luid, uint id)
    {
        byte* b = stackalloc byte[32];
        Header(b, 9, 32, luid, id);
        if (DisplayConfigGetDeviceInfo(b) != 0) return (null, null);
        uint v = *(uint*)(b + 20), bits = *(uint*)(b + 28);
        return ((v & 1) == 0 ? null : (v & 2) != 0, bits is > 0 and <= 16 ? (int)bits : null);
    }

    /// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME (84 bytes): header 20, GDI name WCHAR[32] @20 (like "\\.\DISPLAY1").</summary>
    static string? SourceName(byte* path, uint srcId)
    {
        byte* b = stackalloc byte[84];
        Header(b, 1, 84, path, srcId);
        return DisplayConfigGetDeviceInfo(b) == 0 ? new string((char*)(b + 20)) : null;
    }

    /// <summary>Largest mode the monitor supports (native) and the highest refresh at that size. DEVMODEW: dmSize u16 @68, bits @168 (unused), width @172, height @176, frequency @184.</summary>
    static readonly Dictionary<string, (int, int, double)> modeCache = new();   // the list only changes with another monitor: key = GDI name + monitor name

    static (int W, int H, double Hz) Modes(string? gdiName, string monitor)
    {
        if (gdiName == null) return (0, 0, 0);
        lock (modeCache) if (modeCache.TryGetValue(gdiName + "|" + monitor, out var c)) return c;
        byte* dm = stackalloc byte[220];
        int bw = 0, bh = 0; uint hz = 0;
        for (uint i = 0; i < 4000; i++)
        {
            new Span<byte>(dm, 220).Clear();
            *(ushort*)(dm + 68) = 220;
            if (!EnumDisplaySettingsEx(gdiName, i, dm, 0)) break;
            int w = *(int*)(dm + 172), h = *(int*)(dm + 176); uint f = *(uint*)(dm + 184);
            if ((long)w * h > (long)bw * bh) { bw = w; bh = h; hz = f; }
            else if (w == bw && h == bh && f > hz) hz = f;
        }
        lock (modeCache) modeCache[gdiName + "|" + monitor] = (bw, bh, hz);
        return (bw, bh, hz);
    }

    /// <summary>DPI scaling in percent of the monitor at the source's desktop position; null when unknown.</summary>
    static int? Scale(int x, int y)
    {
        var mon = MonitorFromPoint(x, y, 0 /*MONITOR_DEFAULTTONULL*/);
        int pct = 0;
        return mon != IntPtr.Zero && GetScaleFactorForMonitor(mon, &pct) == 0 && pct > 0 ? pct : null;
    }
}
