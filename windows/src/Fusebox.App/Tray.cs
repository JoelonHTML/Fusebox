using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Fusebox.Core;
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
    const uint CmdOpen = 1, CmdQuit = 2, CmdSettings = 3, CmdStatus = 4;
    static readonly nint HwndMessage = -3;

    static Tray? instance;                 // the window procedure is static; there is one tray icon per process
    readonly uint taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    nint hwnd, icon;
    string tip = "";
    bool added;
    TrayFlyout? flyout;

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
        Add();
        App.Live.Sampled += OnSampled;
        OnSampled();
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

    /// <summary>Cheap: builds a short string, talks to the shell only when the text changed.</summary>
    void OnSampled()
    {
        if (App.Live.Last is not { } s || double.IsNaN(s.Cpu)) return;
        var t = $"Fusebox — CPU {Fmt.Percent(s.Cpu)} · RAM {(s.RamUsed / 1e9).ToString("0.0", Culture)} GB";
        if (t == tip) return;
        tip = t;
        if (added) Notify(NIM_MODIFY);
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

    void ShowMenu()
    {
        GetCursorPos(out var pt);
        var menu = CreatePopupMenu();
        fixed (char* status = App.Live.Status.Text, open = L("Open Fusebox"), settings = L("Settings…"), quit = L("Quit Fusebox"))
        {
            AppendMenuW(menu, 0x1 /* MF_GRAYED */, CmdStatus, status);
            AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, 0, null);
            AppendMenuW(menu, 0, CmdOpen, open);
            AppendMenuW(menu, 0, CmdSettings, settings);
            AppendMenuW(menu, 0, CmdQuit, quit);
            SetForegroundWindow(hwnd);   // otherwise the menu does not close when clicking elsewhere
            uint cmd = TrackPopupMenu(menu, 0x2 /* TPM_RIGHTBUTTON */ | 0x100 /* TPM_RETURNCMD */ | 0x20 /* TPM_BOTTOMALIGN */, pt.X, pt.Y, 0, hwnd, 0);
            PostMessageW(hwnd, 0 /* WM_NULL */, 0, 0);
            DestroyMenu(menu);
            if (cmd == CmdOpen) App.ShowMain();
            else if (cmd == CmdSettings) Settings.SettingsWindow.Open();
            else if (cmd == CmdQuit) App.Quit();
        }
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
                    if (ev == WM_LBUTTONUP) t.ToggleFlyout();
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
