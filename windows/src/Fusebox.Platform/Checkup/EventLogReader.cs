using System.ComponentModel;
using System.Runtime.InteropServices;
using Fusebox.Core.Checkup;

namespace Fusebox.Platform.Checkup;

/// <summary>Windows Event Log through wevtapi.dll (EvtQuery + EvtNext + EvtRender as XML, parsed in Core).</summary>
public static unsafe class EventLogReader
{
    /// <summary>Newest first. Throws Win32Exception when the query itself fails (bad channel or XPath).</summary>
    public static List<EventInfo> Query(string channel, string xpath, int max = 1000)
    {
        var result = new List<EventInfo>();
        var q = CheckupNative.EvtQuery(IntPtr.Zero, channel, xpath, CheckupNative.EvtQueryChannelPath | CheckupNative.EvtQueryReverseDirection);
        if (q == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), $"EvtQuery({channel})");
        try
        {
            var handles = stackalloc IntPtr[32];
            var buf = new byte[16 * 1024];
            while (result.Count < max)
            {
                if (!CheckupNative.EvtNext(q, 32, handles, 2000, 0, out uint got))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == CheckupNative.ErrorNoMoreItems || err == 258 /* timeout */) break;
                    throw new Win32Exception(err, "EvtNext");
                }
                for (int i = 0; i < got; i++)
                {
                    try
                    {
                        var xml = Render(handles[i], ref buf);
                        if (xml != null && CheckupParsers.ParseEvent(xml) is { } e) result.Add(e);
                    }
                    finally { CheckupNative.EvtClose(handles[i]); }
                }
            }
        }
        finally { CheckupNative.EvtClose(q); }
        return result;
    }

    static string? Render(IntPtr evt, ref byte[] buf)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint used, props;
            bool ok;
            fixed (byte* p = buf) ok = CheckupNative.EvtRender(IntPtr.Zero, evt, CheckupNative.EvtRenderEventXml, (uint)buf.Length, p, out used, out props);
            if (ok) return Decode(buf, (int)used);
            if (Marshal.GetLastWin32Error() != CheckupNative.ErrorInsufficientBuffer) return null;
            buf = new byte[used];
        }
        return null;
    }

    static string Decode(byte[] buf, int used) => System.Text.Encoding.Unicode.GetString(buf, 0, Math.Max(0, used - 2));   // minus the terminating NUL

    const string Days30 = "2592000000", Days7 = "604800000";
    static string Within(string ms) => $"TimeCreated[timediff(@SystemTime) <= {ms}]";

    /// <summary>Last 30 days: app crashes (1000), hangs (1002), blue screens (WER 1001), unexpected shutdowns (Kernel-Power 41), low memory (Resource-Exhaustion-Detector 2004). Works from day one: Windows keeps this history itself.</summary>
    public static List<EventInfo> Crashes()
    {
        var all = Query("Application",
            $"*[System[Provider[@Name='Application Error' or @Name='Application Hang'] and (EventID=1000 or EventID=1002) and {Within(Days30)}]]");
        all.AddRange(Query("System",
            $"*[System[((Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004)) and {Within(Days30)}]]"));
        return all.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>Last 7 days: entering sleep (Kernel-Power 42) and wake-ups (Power-Troubleshooter 1).</summary>
    public static List<EventInfo> Sleep() =>
        Query("System",
            $"*[System[((Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=42) or (Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)) and {Within(Days7)}]]");
}
