using System.Runtime.InteropServices;

namespace Fusebox.Platform;

/// <summary>
/// Minimal PDH (Performance Data Helper) wrapper. One query per reader; Collect() once per tick, then read values.
/// Rates (…/sec, % Utilization) need two Collect() calls before the first value. English counter paths (PdhAddEnglishCounter),
/// so it works on Dutch/German/French/Spanish Windows too.
/// </summary>
public sealed unsafe partial class PdhQuery : IDisposable
{
    IntPtr query;
    readonly List<(string path, IntPtr counter)> counters = [];

    public PdhQuery() { if (PdhOpenQueryW(null, 0, out query) != 0) query = 0; }

    /// <summary>Adds a counter path (wildcards like \GPU Engine(*)\Utilization Percentage are fine). Returns an index, or -1.</summary>
    public int Add(string englishPath)
    {
        if (query == 0 || PdhAddEnglishCounterW(query, englishPath, 0, out var c) != 0) return -1;
        counters.Add((englishPath, c));
        return counters.Count - 1;
    }

    public bool Collect() => query != 0 && PdhCollectQueryData(query) == 0;

    /// <summary>Single value of a non-wildcard counter, NaN if not (yet) available.</summary>
    public double Value(int index)
    {
        if (index < 0) return double.NaN;
        return PdhGetFormattedCounterValue(counters[index].counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0 && v.CStatus <= 1 ? v.doubleValue : double.NaN;
    }

    /// <summary>All instances of a wildcard counter: (instance name, value). Empty if not available.</summary>
    public List<(string Instance, double Value)> Values(int index)
    {
        var result = new List<(string, double)>();
        if (index < 0) return result;
        var c = counters[index].counter;
        uint size = 0, count = 0;
        int r = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, null);
        if (r != PDH_MORE_DATA || size == 0) return result;
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            if (PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, p) != 0) return result;
            var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)p;
            for (int i = 0; i < count; i++)
                if (items[i].FmtValue.CStatus <= 1)
                    result.Add((new string(items[i].szName), items[i].FmtValue.doubleValue));
        }
        return result;
    }

    public void Dispose() { if (query != 0) { PdhCloseQuery(query); query = 0; } }

    const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }   // union: double at offset 8

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM_W { public char* szName; public PDH_FMT_COUNTERVALUE FmtValue; }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [LibraryImport("pdh.dll")]
    private static partial int PdhCollectQueryData(IntPtr query);
    [LibraryImport("pdh.dll")]
    private static partial int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [LibraryImport("pdh.dll")]
    private static partial int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, byte* items);
    [LibraryImport("pdh.dll")]
    private static partial int PdhCloseQuery(IntPtr query);
}
