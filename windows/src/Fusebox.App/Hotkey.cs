using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Fusebox.App;

/// <summary>
/// Global shortcut (Prefs "hotkeyCode" = virtual key, −1 = off; "hotkeyMods" = MOD_* flags; default Ctrl+Alt+F) shows or hides
/// the main window. RegisterHotKey posts WM_HOTKEY to the window,
/// which we read through a comctl32 window subclass (no message loop of our own).
/// </summary>
public static unsafe class Hotkey
{
    const int Id = 0x4642;
    static nint hwnd;
    static Window? window;
    static bool installed, registered;

    /// <summary>False when another program already owns the combination.</summary>
    public static bool Registered => registered;

    public static void Start(Window w)
    {
        window = w;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
        Prefs.Changed += k => { if (k is "hotkeyCode" or "hotkeyMods") window?.DispatcherQueue.TryEnqueue(Apply); };
        Apply();
    }

    public static void Apply()
    {
        if (hwnd == 0) return;
        if (!installed) installed = SettingsNative.SetWindowSubclass(hwnd, &WndProc, 1, 0);
        if (registered) { SettingsNative.UnregisterHotKey(hwnd, Id); registered = false; }
        int code = Prefs.Int("hotkeyCode"), mods = Prefs.Int("hotkeyMods") & 0xF;   // MOD_ALT|CONTROL|SHIFT|WIN
        if (code > 0 && mods != 0) registered = SettingsNative.RegisterHotKey(hwnd, Id, (uint)mods | SettingsNative.MOD_NOREPEAT, (uint)code);
    }

    [UnmanagedCallersOnly]   // platform default = stdcall on x64/arm64, matches delegate* unmanaged
    static nint WndProc(nint h, uint msg, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == SettingsNative.WM_HOTKEY && (int)wParam == Id)
        {
            window?.DispatcherQueue.TryEnqueue(Toggle);
            return 0;
        }
        return SettingsNative.DefSubclassProc(h, msg, wParam, lParam);
    }

    /// <summary>In front → hide (keeps measuring). Otherwise show, restore and bring to front.</summary>
    public static void Toggle()
    {
        if (window == null) return;
        var aw = window.AppWindow;
        bool minimized = aw.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (aw.IsVisible && !minimized && SettingsNative.GetForegroundWindow() == hwnd)
        {
            // Hide to the tray; without a tray icon minimize, so the taskbar button stays (never unreachable).
            if (App.Tray is { Visible: true }) aw.Hide(); else if (aw.Presenter is OverlappedPresenter op) op.Minimize();
            return;
        }
        aw.Show();
        if (aw.Presenter is OverlappedPresenter p && minimized) p.Restore();
        window.Activate();
        SettingsNative.SetForegroundWindow(hwnd);
    }
}
