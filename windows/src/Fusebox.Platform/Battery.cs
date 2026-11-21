using System.Runtime.InteropServices;
using Fusebox.Core;

namespace Fusebox.Platform;

/// <summary>
/// Battery: GetSystemPowerStatus every tick (percent, AC), plus the battery device (SetupAPI + IOCTL) for capacity, cycles and watts.
/// No battery (desktop, CI VM) = everything stays NaN and nothing else is opened.
/// </summary>
internal sealed unsafe partial class BatteryReader : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte AC, Flags, Percent, Saver; public uint LifeTime, FullLifeTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_QUERY_INFORMATION { public uint Tag, Level, AtRate; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_INFORMATION { public uint Capabilities; public byte Technology, R1, R2, R3, C0, C1, C2, C3; public uint Designed, Full, Alert1, Alert2, CriticalBias, Cycles; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_WAIT_STATUS { public uint Tag, Timeout, PowerState, Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_STATUS { public uint PowerState, Capacity, Voltage; public int Rate; }

    const uint IOCTL_TAG = 0x294040, IOCTL_INFO = 0x294044, IOCTL_STATUS = 0x29404C;
    const uint Unknown = 0xFFFFFFFF;

    nint handle = -1;
    uint tag;
    bool noDevice;          // gave up: no battery device
    int ticks;
    double design = double.NaN, full = double.NaN, cycles = double.NaN;

    public void Read(LiveSample s)
    {
        if (!GetSystemPowerStatus(out var p) || (p.Flags & 128) != 0 || p.Percent > 100) return;   // 128 = no battery, 255 = unknown
        s.Battery = p.Percent;
        s.OnAc = p.AC == 1;
        s.Charging = (p.Flags & 8) != 0;
        if (p.LifeTime != Unknown && !s.OnAc) s.BatteryMinutes = p.LifeTime / 60.0;
        if (!Open()) return;
        var wait = new BATTERY_WAIT_STATUS { Tag = tag };
        BATTERY_STATUS st;
        if (!DeviceIoControl(handle, IOCTL_STATUS, &wait, (uint)sizeof(BATTERY_WAIT_STATUS), &st, (uint)sizeof(BATTERY_STATUS), out _, 0)) { Close(); return; }
        if (st.Rate != int.MinValue) s.BatteryMw = st.Rate;
        if (ticks++ % 30 == 0) ReadInfo();                    // capacity and cycles barely change
        s.BatteryDesign = design; s.BatteryFull = full; s.BatteryCycles = cycles;
        if (s.Charging && st.Rate > 0 && full > st.Capacity) s.BatteryMinutes = (full - st.Capacity) / st.Rate * 60;   // time to full
        else if (!s.OnAc && double.IsNaN(s.BatteryMinutes)) s.BatteryMinutes = EstimatedMinutes();
    }

    void ReadInfo()
    {
        var q = new BATTERY_QUERY_INFORMATION { Tag = tag, Level = 0 };
        BATTERY_INFORMATION bi;
        if (!DeviceIoControl(handle, IOCTL_INFO, &q, (uint)sizeof(BATTERY_QUERY_INFORMATION), &bi, (uint)sizeof(BATTERY_INFORMATION), out _, 0)) return;
        design = bi.Designed; full = bi.Full;
        cycles = bi.Cycles > 0 ? bi.Cycles : double.NaN;      // many laptops report 0 = unknown
    }

    double EstimatedMinutes()
    {
        var q = new BATTERY_QUERY_INFORMATION { Tag = tag, Level = 3 /* BatteryEstimatedTime */ };
        uint sec;
        return DeviceIoControl(handle, IOCTL_INFO, &q, (uint)sizeof(BATTERY_QUERY_INFORMATION), &sec, 4, out _, 0) && sec != Unknown ? sec / 60.0 : double.NaN;
    }

    bool Open()
    {
        if (handle != -1) return true;
        if (noDevice) return false;
        // ponytail: first battery only; extra batteries (rare) are ignored.
        var guid = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");   // GUID_DEVCLASS_BATTERY
        var set = SetupDiGetClassDevsW(&guid, null, 0, 0x2 | 0x10);    // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (set == -1) { noDevice = true; return false; }
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
            if (!SetupDiEnumDeviceInterfaces(set, 0, &guid, 0, &data)) { noDevice = true; return false; }
            uint need = 0;
            SetupDiGetDeviceInterfaceDetailW(set, &data, null, 0, &need, 0);
            if (need == 0) { noDevice = true; return false; }
            var buf = new byte[need];
            fixed (byte* b = buf)
            {
                *(uint*)b = (uint)(sizeof(nint) == 8 ? 8 : 6);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                if (!SetupDiGetDeviceInterfaceDetailW(set, &data, b, need, &need, 0)) { noDevice = true; return false; }
                handle = CreateFileW((char*)(b + 4), 0xC0000000, 3, 0, 3, 0x80, 0);
            }
            if (handle == -1) return false;
            uint wait = 0, t = 0;
            if (!DeviceIoControl(handle, IOCTL_TAG, &wait, 4, &t, 4, out _, 0) || t == 0) { Close(); noDevice = true; return false; }
            tag = t;
            return true;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    void Close() { if (handle != -1) { CloseHandle(handle); handle = -1; } }
    public void Dispose() => Close();

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public nuint Reserved; }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateFileW(char* name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint h, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, nint overlapped);
    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial nint SetupDiGetClassDevsW(Guid* classGuid, char* enumerator, nint parent, uint flags);
    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint set, nint info, Guid* classGuid, uint index, SP_DEVICE_INTERFACE_DATA* data);
    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetailW(nint set, SP_DEVICE_INTERFACE_DATA* data, byte* detail, uint size, uint* required, nint info);
    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);
}
