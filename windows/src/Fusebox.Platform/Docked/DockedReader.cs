using Fusebox.Core.Docked;
using static Fusebox.Platform.Docked.DockedNative;

namespace Fusebox.Platform.Docked;

/// <summary>Reads everything the Docked page shows. Call on a background thread; each section fails on its own.</summary>
public static unsafe class DockedReader
{
    /// <summary>Per-section errors are collected here (for the probe), never thrown.</summary>
    public static DockedSnapshot Read(List<string>? errors = null)
    {
        T Try<T>(string what, Func<T> f, T fallback)
        {
            try { return f(); } catch (Exception e) { errors?.Add($"{what}: {e.GetType().Name} {e.Message}"); return fallback; }
        }
        return new DockedSnapshot(
            Try("power", ReadPower, new PowerInfo(null, null, false, false)),
            Try("usb", Usb.Read, new List<UsbNode>()),
            Try("displays", Displays.Read, new List<DisplayInfo>()),
            Try<List<BtDevice>?>("bluetooth", Bluetooth.Read, null),
            Try("audio", Audio.Read, new List<AudioEndpoint>()));
    }

    static PowerInfo ReadPower()
    {
        SYSTEM_POWER_STATUS s;
        if (!GetSystemPowerStatus(&s)) throw new InvalidOperationException("GetSystemPowerStatus failed");
        return PowerMap.From(s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent);
    }
}
