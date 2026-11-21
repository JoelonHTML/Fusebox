using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fusebox.Core;
using Fusebox.Core.Live;
using Microsoft.Win32;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>
/// System tray icon (Shell_NotifyIconW) with a hidden message-only window; the Windows counterpart of the Mac menu bar item.
/// Left click = flyout (<see cref="TrayFlyout"/>), right click = menu (status, Open, Settings, Quit). The window procedure is an [UnmanagedCallersOnly]
/// function pointer (AOT-safe, no delegates to keep alive). Everything runs on the UI thread.
/// </summary>
public sealed unsafe partial class Tray : IDisposable
{
    const uint WM_APP_TRAY = 0x8001, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint CmdOpen = 1, CmdQuit = 2, CmdSettings = 3, CmdStatus = 4, CmdCheckup = 5, CmdSetup = 6, CmdValue = 100;
    static readonly nint HwndMessage = -3;

    static Tray? instance;                 // the window procedure is static; there is one tray icon per process
    readonly uint taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    nint hwnd, icon;
    string tip = "";
    bool added;
    TrayFlyout? flyout;
    uint[]? lastPixels;                    // the icon image now shown: the HICON is rebuilt only when this changes
    bool lightTaskbar;
    int iconSize = 16;

    public bool FlyoutOpen => flyout?.IsOpen ?? false;
    public bool Visible => added;

    public Tray()
    {
        instance = this;
        fixed (char* cls = "FuseboxTray")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WndProc,
                hInstance = GetModuleHandleW(null),
                lpszClassName = cls,
            };
            RegisterClassExW(&wc);
            hwnd = CreateWindowExW(0, cls, cls, 0, 0, 0, 0, 0, HwndMessage, 0, wc.hInstance, 0);
        }
        int size = GetSystemMetrics(49 /* SM_CXSMICON */);
        fixed (char* path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fusebox.ico"))
            icon = LoadImageW(0, path, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        iconSize = Math.Max(size, 16);
        lightTaskbar = ReadLightTaskbar();
        Add();
        App.Live.Sampled += OnSampled;
        Prefs.Changed += OnPref;
        ThemeService.ThemeChanged += OnTheme;
        OnSampled();
    }

    // Style, metrics or the Windows taskbar theme changed: draw again now instead of at the next sample.
    void OnPref(string key) { if (key is "menubarStyle" or "menubarMetrics" or "menubarSplit" or "absolute" or "netBits" or "metrics") Redraw(); }
    void OnTheme() { lightTaskbar = ReadLightTaskbar(); Redraw(); }
    void Redraw() { lastPixels = null; tip = ""; App.Main?.DispatcherQueue.TryEnqueue(OnSampled); }

    /// <summary>Glyph colour: dark ink on a light taskbar (SystemUsesLightTheme = 1), white otherwise.</summary>
    static bool ReadLightTaskbar()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v == 1; }
        catch { return false; }
    }

    /// <summary>Show or remove the icon (Settings).</summary>
    public void SetVisible(bool visible)
    {
        if (visible && !added) Add();
        else if (!visible && added) { Notify(NIM_DELETE); added = false; }
    }

    void Add()
    {
        added = Notify(NIM_ADD);
    }

    bool Notify(uint message)
    {
        var d = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP, uCallbackMessage = WM_APP_TRAY, hIcon = icon };
        int n = Math.Min(tip.Length, 127);
        for (int i = 0; i < n; i++) d.szTip[i] = tip[i];
        return Shell_NotifyIconW(message, &d);
    }

    /// <summary>
    /// Every sample (watched or not): builds the items, draws the icon into a pixel buffer and talks to the shell only when the image or the
    /// tooltip really changed (~1 ms, no allocation of an HICON otherwise). Style from menubarStyle, metrics from menubarMetrics (max 4).
    /// Split items (menubarSplit) are not drawn: one icon only.
    /// </summary>
    void OnSampled()
    {
        if (App.Live.Last is not { } s || double.IsNaN(s.Cpu)) return;
        string style = Prefs.Str("menubarStyle");
        if (style == "off") return;
        var o = MetricText.Options();
        var metrics = TrayInfo.Pick(Prefs.Str("menubarMetrics"), m => !double.IsNaN(Metrics.Value(m, s)), 4);
        if (metrics.Count == 0) metrics = [Metric.Cpu];
        bool graph = style == "graph";
        var recent = graph ? App.Live.History.Recent : [];
        var items = metrics.Select(m => new TrayItem(Metrics.Label(m), TrayInfo.MenuValue(m, s, o), TrayInfo.Fraction(m, s),
            graph ? recent.Skip(Math.Max(recent.Count - 30, 0)).Select(x => TrayInfo.Fraction(m, x)).ToList() : [])).ToList();
        var text = TrayInfo.Tooltip(items);
        var pixels = TrayGlyph.Render(style, iconSize, lightTaskbar, items, style == "icon" && App.Live.Status.Level > 0);
        bool sameImage = lastPixels != null && lastPixels.AsSpan().SequenceEqual(pixels);
        if (sameImage && text == tip) return;
        tip = text;
        if (!sameImage)
        {
            var fresh = MakeIcon(pixels, iconSize);
            if (fresh != 0)
            {
                var old = icon;
                icon = fresh; lastPixels = pixels;
                if (added) Notify(NIM_MODIFY);
                if (old != 0) DestroyIcon(old);
                return;
            }
        }
        if (added) Notify(NIM_MODIFY);
    }

    /// <summary>ARGB pixels → HICON (32-bit colour bitmap with alpha + empty mask). Premultiplied, as the shell blends it.</summary>
    static nint MakeIcon(uint[] px, int size)
    {
        var pre = new uint[px.Length];
        for (int i = 0; i < px.Length; i++)
        {
            uint a = px[i] >> 24, r = (px[i] >> 16 & 0xFF) * a / 255, g = (px[i] >> 8 & 0xFF) * a / 255, b = (px[i] & 0xFF) * a / 255;
            pre[i] = a << 24 | r << 16 | g << 8 | b;
        }
        var mask = new byte[(size + 15) / 16 * 2 * size];
        nint color, mono;
        fixed (uint* p = pre) color = CreateBitmap(size, size, 1, 32, p);
        fixed (byte* m = mask) mono = CreateBitmap(size, size, 1, 1, m);
        nint hicon = 0;
        if (color != 0 && mono != 0)
        {
            var ii = new ICONINFO { fIcon = 1, hbmMask = mono, hbmColor = color };
            hicon = CreateIconIndirect(&ii);
        }
        if (color != 0) DeleteObject(color);
        if (mono != 0) DeleteObject(mono);
        return hicon;
    }

    void ToggleFlyout()
    {
        flyout ??= new TrayFlyout();
        flyout.Toggle();
    }

    /// <summary>The icon's screen rectangle (physical pixels), or null when the shell doesn't say.</summary>
    public Fusebox.Core.Look.Box? IconRect()
    {
        if (!added) return null;
        var id = new Win32.NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(Win32.NOTIFYICONIDENTIFIER), hWnd = hwnd, uID = 1 };
        Win32.RECT r;
        if (Win32.Shell_NotifyIconGetRect(&id, &r) != 0 || r.Right <= r.Left) return null;
        return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    static void Item(nint menu, uint flags, nuint id, string text)
    {
        fixed (char* t = text) AppendMenuW(menu, flags, id, t);
    }

    /// <summary>
    /// The tray menu (§8.4), in this order, each part when enabled in menuParts: status (disabled), one disabled line per Live metric
    /// ("CPU   23%  52°"), "Heaviest (last minute)" with the 3 heaviest apps, a divider, then "Why is my PC slow?", "Open Fusebox", "Setup…"
    /// (links) and always "Settings…" and "Quit Fusebox".
    /// </summary>
    void ShowMenu()
    {
        GetCursorPos(out var pt);
        var parts = Prefs.List("menuParts");
        var menu = CreatePopupMenu();
        const uint Gray = 0x1, Sep = 0x800;
        if (parts.Contains("status")) Item(menu, Gray, CmdStatus, App.Live.Status.Text);
        if (parts.Contains("values") && App.Live.Last is { } s)
        {
            var o = MetricText.Options();
            uint id = CmdValue;
            foreach (var m in MetricText.Enabled(s))
            {
                var c = LiveCards.Card(m, s, o);
                string unit = c.Unit.Length > 0 ? (c.Unit.StartsWith('/') ? c.Unit : " " + c.Unit) : "";
                Item(menu, Gray, id++, $"{Metrics.Label(m)}   {(c.Value.Length == 0 ? "–" : c.Value)}{unit}{(c.Side.Length > 0 ? "  " + c.Side : "")}");
            }
        }
        if (parts.Contains("top") && App.Live.Apps is { } apps)
        {
            var top = apps.Where(g => g.Kind != Fusebox.Core.Processes.AppKind.Windows).OrderByDescending(g => g.Cpu).Take(3).ToList();
            if (top.Count > 0)
            {
                Item(menu, Sep, 0, "");
                Item(menu, Gray, CmdStatus, L("Heaviest (last minute)"));
                uint id = CmdValue + 20;
                foreach (var g in top) Item(menu, Gray, id++, $"{g.Name}   {Fmt.Pct(g.Cpu)}");
            }
        }
        Item(menu, Sep, 0, "");
        if (parts.Contains("links"))
        {
            Item(menu, 0, CmdCheckup, L("Why is my PC slow?"));
            Item(menu, 0, CmdOpen, L("Open Fusebox"));
            Item(menu, 0, CmdSetup, L("Setup…"));
        }
        Item(menu, 0, CmdSettings, L("Settings…"));
        Item(menu, 0, CmdQuit, L("Quit Fusebox"));
        SetForegroundWindow(hwnd);   // otherwise the menu does not close when clicking elsewhere
        uint cmd = TrackPopupMenu(menu, 0x2 /* TPM_RIGHTBUTTON */ | 0x100 /* TPM_RETURNCMD */ | 0x20 /* TPM_BOTTOMALIGN */, pt.X, pt.Y, 0, hwnd, 0);
        PostMessageW(hwnd, 0 /* WM_NULL */, 0, 0);
        DestroyMenu(menu);
        if (cmd == CmdOpen) App.ShowMain();
        else if (cmd == CmdCheckup) App.ShowMain("Checkup");
        else if (cmd == CmdSetup) Boot.ShowSetup();
        else if (cmd == CmdSettings) Settings.SettingsWindow.Open();
        else if (cmd == CmdQuit) App.Quit();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            var t = instance;
            if (t != null)
            {
                if (msg == WM_APP_TRAY)
                {
                    uint ev = (uint)lParam & 0xFFFF;
                    if (ev == WM_LBUTTONUP) { if (Prefs.Str("menuStyle") == "menu") t.ShowMenu(); else t.ToggleFlyout(); }
                    else if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU) t.ShowMenu();
                    return 0;
                }
                if (msg == t.taskbarCreated && t.added) { t.Add(); return 0; }   // Explorer restarted: put the icon back
            }
        }
        catch { }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        App.Live.Sampled -= OnSampled;
        Prefs.Changed -= OnPref;
        ThemeService.ThemeChanged -= OnTheme;
        if (added) { Notify(NIM_DELETE); added = false; }
        if (hwnd != 0) { DestroyWindow(hwnd); hwnd = 0; }
        if (icon != 0) { DestroyIcon(icon); icon = 0; }
        instance = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public int fIcon; public uint xHotspot, yHotspot; public nint hbmMask, hbmColor; }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int w, int h, uint planes, uint bits, void* data);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);
    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(ICONINFO* info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
    [LibraryImport("kernel32.dll")]
    private static partial nint GetModuleHandleW(char* name);
    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);
    [LibraryImport("user32.dll")]
    private static partial nint LoadImageW(nint inst, char* name, uint type, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, char* text);
    [LibraryImport("user32.dll")]
    private static partial uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);
}
