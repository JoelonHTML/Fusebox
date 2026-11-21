using System.Runtime.InteropServices;
using Microsoft.Win32;
using static Fusebox.Platform.Native;

namespace Fusebox.Platform;

/// <summary>Installed apps from the Uninstall registry keys (HKCU + HKLM + WOW6432Node). Read on demand (when the user searches), not every tick.</summary>
public static class InstalledApps
{
    public static List<string> All()
    {
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var (hive, sub) in new[] {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") })
        {
            try
            {
                using var root = hive.OpenSubKey(sub);
                if (root == null) continue;
                foreach (var k in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(k);
                    if (key?.GetValue("DisplayName") is not string n || n.Length == 0) continue;
                    if (key.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (key.GetValue("ParentKeyName") != null) continue;   // updates / patches
                    names.Add(n);
                }
            }
            catch { }
        }
        return names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

/// <summary>An icon as top-down premultiplied BGRA pixels, ready for a WriteableBitmap.</summary>
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

public static unsafe class IconReader
{
    const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0, DI_NORMAL = 3, BI_RGB = 0, DIB_RGB_COLORS = 0;
    const int Size = 32;

    /// <summary>Shell icon of an exe (32×32). Null if it can't be read. Cheap enough to call once per exe path.</summary>
    public static IconPixels? Read(string path)
    {
        var sfi = new SHFILEINFOW();
        if (SHGetFileInfo(path, 0, ref sfi, (uint)sizeof(SHFILEINFOW), SHGFI_ICON | SHGFI_LARGEICON) == 0 || sfi.hIcon == 0) return null;
        IntPtr dc = CreateCompatibleDC(0), bmp = 0, old = 0;
        try
        {
            var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = Size, biHeight = -Size, biPlanes = 1, biBitCount = 32, biCompression = BI_RGB };
            bmp = CreateDIBSection(dc, ref bi, DIB_RGB_COLORS, out var bits, 0, 0);
            if (bmp == 0 || bits == 0) return null;
            old = SelectObject(dc, bmp);
            if (!DrawIconEx(dc, 0, 0, sfi.hIcon, Size, Size, 0, 0, DI_NORMAL)) return null;
            var px = new byte[Size * Size * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;   // old icons without an alpha channel
            return new IconPixels(Size, Size, px);
        }
        finally
        {
            if (old != 0) SelectObject(dc, old);
            if (bmp != 0) DeleteObject(bmp);
            DeleteDC(dc);
            DestroyIcon(sfi.hIcon);
        }
    }
}
