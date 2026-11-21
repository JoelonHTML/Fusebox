using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Fusebox.App;

/// <summary>
/// Ctrl+Alt+Win+F shows or hides the main window (Prefs "hotkey", default on). RegisterHotKey posts WM_HOTKEY to the window,
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
        Prefs.Changed += k => { if (k == "hotkey") Apply(); };
        Apply();
    }

    public static void Apply()
    {
        if (hwnd == 0) return;
        if (!installed) installed = SettingsNative.SetWindowSubclass(hwnd, &WndProc, 1, 0);
        if (registered) { SettingsNative.UnregisterHotKey(hwnd, Id); registered = false; }
        if (Prefs.Get("hotkey", true))
            registered = SettingsNative.RegisterHotKey(hwnd, Id, SettingsNative.MOD_CONTROL | SettingsNative.MOD_ALT | SettingsNative.MOD_WIN | SettingsNative.MOD_NOREPEAT, 'F');
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
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
        if (aw.IsVisible && !minimized && SettingsNative.GetForegroundWindow() == hwnd) { aw.Hide(); return; }
        aw.Show();
        if (aw.Presenter is OverlappedPresenter p && minimized) p.Restore();
        window.Activate();
        SettingsNative.SetForegroundWindow(hwnd);
    }
}
