using System.Runtime.InteropServices;
using Fusebox.Core;

namespace Fusebox.Platform;

/// <summary>Disk throughput via PDH (all physical disks) and free/total space of the system drive.</summary>
internal sealed partial class DiskReader(PdhQuery q)
{
    readonly int read = q.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
    readonly int write = q.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
    readonly string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public void Read(LiveSample s)
    {
        s.DiskRead = q.Value(read);
        s.DiskWrite = q.Value(write);
        if (GetDiskFreeSpaceExW(root, out var free, out var total, out _)) { s.DiskFree = free; s.DiskTotal = total; }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string dir, out ulong freeToCaller, out ulong total, out ulong totalFree);
}
