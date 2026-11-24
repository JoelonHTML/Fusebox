using System.Runtime.InteropServices;
using Fusebox.Core.Processes;
using Microsoft.Win32;
using static Fusebox.Platform.Native;

namespace Fusebox.Platform;

/// <summary>Installed apps for the search: Start Menu shortcuts (what people call apps) plus the Uninstall registry keys (HKCU + HKLM + WOW6432Node),
/// which also give an exe path for the icon and the About card. Read on demand (first keystroke), off the UI thread. Store/AppX apps are not listed.</summary>
public static class InstalledApps
{
    const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> All()
    {
        var fromRegistry = new List<InstalledApp>();
        foreach (var (hive, sub) in new[] { (Registry.CurrentUser, Uninstall), (Registry.LocalMachine, Uninstall), (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") })
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
                    fromRegistry.Add(new(n.Trim(), ExeOf(key.GetValue("DisplayIcon") as string)));
                }
            }
            catch { }
        }

        var result = new List<InstalledApp>();
        foreach (var name in StartMenuNames())
        {
            // A shortcut name and a registry name that contain each other are the same app ("Visual Studio Code" / "Microsoft Visual Studio Code").
            var match = fromRegistry.FirstOrDefault(r => r.Path != null && (r.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase) || name.Contains(r.Name, StringComparison.CurrentCultureIgnoreCase)));
            result.Add(new(name, match?.Path));
        }
        foreach (var r in fromRegistry)
            if (!result.Any(a => a.Name.Contains(r.Name, StringComparison.CurrentCultureIgnoreCase) || r.Name.Contains(a.Name, StringComparison.CurrentCultureIgnoreCase)))
                result.Add(r);
        return result.GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => g.OrderByDescending(a => a.Path != null).First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>DisplayIcon is usually <c>"C:\x\app.exe",0</c>; only an existing .exe counts.</summary>
    static string? ExeOf(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var p = icon.Trim().Trim('"');
        int comma = p.LastIndexOf(',');
        if (comma > 1 && int.TryParse(p[(comma + 1)..], out _)) p = p[..comma];
        p = p.Trim().Trim('"');
        return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p) ? p : null;
    }

    static IEnumerable<string> StartMenuNames()
    {
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    if (n.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || n.Contains("readme", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(n);
                }
            }
            catch { }
        }
        return names;
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
