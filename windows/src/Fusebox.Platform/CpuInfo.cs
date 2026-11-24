using System.Runtime.InteropServices;
using Fusebox.Core;
using Microsoft.Win32;

namespace Fusebox.Platform;

/// <summary>CPU name and P/E core classes (read once).</summary>
internal static unsafe partial class CpuInfo
{
    public static HardwareInfo Read()
    {
        string name = "";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
        }
        catch { }

        // Records of RelationProcessorCore: { uint Relationship; uint Size; byte Flags; byte EfficiencyClass; byte[20]; ushort GroupCount; GROUP_AFFINITY[] }.
        // GROUP_AFFINITY = { nuint Mask; ushort Group; ... } — mask of the first group at offset 32.
        var effLogical = new bool[Environment.ProcessorCount];
        int p = 0, e = 0;
        uint len = 0;
        GetLogicalProcessorInformationEx(0, null, ref len);
        if (len > 0)
        {
            var buf = new byte[len];
            fixed (byte* b = buf)
            {
                if (GetLogicalProcessorInformationEx(0, b, ref len))
                {
                    int max = 0;
                    for (uint off = 0; off + 40 <= len;)
                    {
                        max = Math.Max(max, b[off + 9]);
                        off += *(uint*)(b + off + 4);
                    }
                    for (uint off = 0; off + 40 <= len;)
                    {
                        bool eff = b[off + 9] < max;
                        if (eff) e++; else p++;
                        ulong mask = *(ulong*)(b + off + 32);   // ponytail: first processor group only
                        for (int bit = 0; bit < 64 && bit < effLogical.Length; bit++)
                            if ((mask >> bit & 1) != 0) effLogical[bit] = eff;
                        off += *(uint*)(b + off + 4);
                    }
                }
            }
        }
        return new HardwareInfo { CpuName = name, GpuName = GpuReader.Name(), PerformanceCores = p, EfficiencyCores = e, EfficiencyLogical = effLogical };
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);
}
