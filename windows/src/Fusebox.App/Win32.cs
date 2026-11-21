using System.Runtime.InteropServices;

namespace Fusebox.App;

/// <summary>Small Win32 imports for the window shell and the tray flyout (LibraryImport = AOT-safe).</summary>
internal static unsafe partial class Win32
{
    public const uint WM_GETMINMAXINFO = 0x0024, WM_EXITSIZEMOVE = 0x0232;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER { public uint cbSize; public nint hWnd; public uint uID; public Guid guidItem; }

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    /// <summary>Screen rectangle of a tray icon (physical pixels). 0 = S_OK.</summary>
    [LibraryImport("shell32.dll")]
    public static partial int Shell_NotifyIconGetRect(NOTIFYICONIDENTIFIER* id, RECT* rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT p);

    public static double Scale(nint hwnd) { uint dpi = GetDpiForWindow(hwnd); return dpi == 0 ? 1 : dpi / 96.0; }
}
