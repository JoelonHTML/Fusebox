using System.Runtime.InteropServices;

namespace Fusebox.Platform;

/// <summary>Win32 imports shared by the readers. LibraryImport = AOT-friendly, no runtime marshalling stubs.</summary>
internal static unsafe partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime, KernelTime, UserTime, DpcTime, InterruptTime;
        public uint InterruptCount;
    }

    public const int SystemProcessorPerformanceInformation = 8;

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int infoClass, void* info, int length, out int returnLength);

    /// <summary>MIB_IF_ROW2 from netioapi.h (1352 bytes on x64/arm64; the probe checks the size).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte InterfaceAndOperStatusFlags;   // bit0 HardwareInterface, bit1 FilterInterface, …
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
        public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos, InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors, OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }

    [LibraryImport("iphlpapi.dll")]
    public static partial int GetIfTable2(out IntPtr table);

    [LibraryImport("iphlpapi.dll")]
    public static partial void FreeMibTable(IntPtr memory);
}
