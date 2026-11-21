using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Fusebox.App;

/// <summary>Win32 imports for hotkey, EcoQoS, message box and the Start menu shortcut. LibraryImport + GeneratedComInterface = AOT-safe.</summary>
internal static unsafe partial class SettingsNative
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const uint WM_HOTKEY = 0x0312;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(nint hwnd, string text, string caption, uint type);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> proc, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    public static partial nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(nint process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    /// <summary>EcoQoS = Windows "Efficiency mode" for this process. Off = back to system-managed.</summary>
    public static void SetEfficiencyMode(bool on)
    {
        var s = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = on ? 1u : 0, StateMask = on ? 1u : 0 };
        SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref s, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
    }

    // ---- Start menu shortcut (IShellLinkW + IPersistFile) ----

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint obj);

    public static void CreateShortcut(string lnkPath, string target, string workDir)
    {
        CoInitializeEx(0, 2);   // apartment threaded; S_FALSE / RPC_E_CHANGED_MODE are fine
        var clsid = new Guid("00021401-0000-0000-C000-000000000046");
        var iid = new Guid("000214F9-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out var p));
        var link = (IShellLinkW)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(p, CreateObjectFlags.None);
        link.SetPath(target);
        link.SetWorkingDirectory(workDir);
        link.SetDescription("Fusebox");
        link.SetIconLocation(target, 0);
        ((IPersistFile)link).Save(lnkPath, true);
    }
}

[GeneratedComInterface, Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    // vtable order matters; unused methods only need the right slot.
    [PreserveSig] int GetPath(nint file, int max, nint fd, uint flags);
    [PreserveSig] int GetIDList(out nint pidl);
    [PreserveSig] int SetIDList(nint pidl);
    [PreserveSig] int GetDescription(nint name, int max);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int GetWorkingDirectory(nint dir, int max);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    [PreserveSig] int GetArguments(nint args, int max);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    [PreserveSig] int GetHotkey(out ushort hotkey);
    [PreserveSig] int SetHotkey(ushort hotkey);
    [PreserveSig] int GetShowCmd(out int show);
    [PreserveSig] int SetShowCmd(int show);
    [PreserveSig] int GetIconLocation(nint path, int max, out int icon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
    [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
    [PreserveSig] int Resolve(nint hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[GeneratedComInterface, Guid("0000010c-0000-0000-C000-000000000046")]
internal partial interface IPersist
{
    void GetClassID(out Guid classId);
}

[GeneratedComInterface, Guid("0000010b-0000-0000-C000-000000000046")]
internal partial interface IPersistFile : IPersist
{
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
    void GetCurFile(out nint file);
}
