using System.Runtime.InteropServices;

namespace Fusebox.Platform.Checkup;

/// <summary>P/Invoke used only by Checkup (own file so it never collides with the shared Native.cs).</summary>
internal static unsafe partial class CheckupNative
{
    // ---- Event Log (wevtapi) ----
    public const uint EvtQueryChannelPath = 0x1, EvtQueryReverseDirection = 0x200, EvtRenderEventXml = 1;
    public const int ErrorNoMoreItems = 259, ErrorInsufficientBuffer = 122;

    [LibraryImport("wevtapi.dll", EntryPoint = "EvtQuery", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr EvtQuery(IntPtr session, string path, string query, uint flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtNext(IntPtr resultSet, uint size, IntPtr* events, uint timeout, uint flags, out uint returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtRender(IntPtr context, IntPtr fragment, uint flags, uint bufferSize, void* buffer, out uint used, out uint propertyCount);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtClose(IntPtr handle);

    // ---- Directory walking ----
    [StructLayout(LayoutKind.Sequential)]
    public struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        public fixed char cFileName[260];
        public fixed char cAlternateFileName[14];
    }

    public const int FindExInfoBasic = 1, FindExSearchNameMatch = 0, FindFirstExLargeFetch = 2;
    public const uint AttrDirectory = 0x10, AttrReparsePoint = 0x400;

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindFirstFileExW(string fileName, int infoLevel, out WIN32_FIND_DATAW data, int searchOp, IntPtr filter, int flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextFileW(IntPtr handle, out WIN32_FIND_DATAW data);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(IntPtr handle);

    // ---- Low priority for the scan thread ----
    public const int ThreadModeBackgroundBegin = 0x00010000, ThreadModeBackgroundEnd = 0x00020000;

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadPriority(IntPtr thread, int priority);

    // ---- Battery presence ----
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
