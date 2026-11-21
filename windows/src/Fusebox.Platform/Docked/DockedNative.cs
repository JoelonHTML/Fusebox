using System.Runtime.InteropServices;

namespace Fusebox.Platform.Docked;

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY
{
    public Guid Fmtid; public uint Pid;
    public DEVPROPKEY(string g, uint pid) { Fmtid = new Guid(g); Pid = pid; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SP_DEVINFO_DATA
{
    public uint CbSize; public Guid ClassGuid; public uint DevInst; public nuint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SP_DEVICE_INTERFACE_DATA
{
    public uint CbSize; public Guid InterfaceClassGuid; public uint Flags; public nuint Reserved;
}

internal static unsafe partial class DockedNative
{
    public const uint DIGCF_PRESENT = 2, DIGCF_ALLCLASSES = 4, DIGCF_DEVICEINTERFACE = 0x10;
    public static readonly IntPtr InvalidHandle = new(-1);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr SetupDiGetClassDevs(Guid* classGuid, string? enumerator, IntPtr hwnd, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInfo(IntPtr set, uint index, SP_DEVINFO_DATA* data);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(IntPtr set, SP_DEVINFO_DATA* info, Guid* iface, uint index, SP_DEVICE_INTERFACE_DATA* data);

    /// <summary>detail = SP_DEVICE_INTERFACE_DETAIL_DATA_W: DWORD cbSize (8 on x64/arm64, 6 on x86) then WCHAR path[].</summary>
    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetail(IntPtr set, SP_DEVICE_INTERFACE_DATA* data, byte* detail, uint size, uint* required, SP_DEVINFO_DATA* info);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceProperty(IntPtr set, SP_DEVINFO_DATA* info, DEVPROPKEY* key, uint* type, byte* buf, uint size, uint* required, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    public static partial uint CM_Get_Parent(uint* parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    public static partial uint CM_Get_Device_ID(uint devInst, char* buf, uint len, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(IntPtr h, uint code, byte* inBuf, uint inSize, byte* outBuf, uint outSize, uint* returned, IntPtr overlapped);

    [LibraryImport("user32.dll")]
    public static partial int GetDisplayConfigBufferSizes(uint flags, uint* paths, uint* modes);

    [LibraryImport("user32.dll")]
    public static partial int QueryDisplayConfig(uint flags, uint* paths, byte* pathInfo, uint* modes, byte* modeInfo, IntPtr topology);

    [LibraryImport("user32.dll")]
    public static partial int DisplayConfigGetDeviceInfo(byte* packet);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(SYSTEM_POWER_STATUS* s);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(Guid* clsid, IntPtr outer, uint ctx, Guid* iid, void** obj);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(IntPtr p);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(byte* pv);
}
