using Fusebox.Core.Update;
using Microsoft.UI.Dispatching;
using Windows.System.Power;

namespace Fusebox.App;

/// <summary>
/// Low impact. Choice = Prefs "lowImpactMode" (off / auto / on, default auto); the effective result is Prefs "lowImpact" (bool, read by LiveHub).
/// Automatic = Energy saver on, or on battery below 20%. Event driven (PowerManager), no polling.
/// When on: measure every 5 s, Efficiency mode (EcoQoS) for this process, no acrylic glass.
/// </summary>
public static class LowImpact
{
    static DispatcherQueue? ui;
    static bool? applied;
    public static event Action? Changed;
    public static bool On => Prefs.Get("lowImpact", false);

    public static void Start()
    {
        ui = DispatcherQueue.GetForCurrentThread();
        try
        {
            PowerManager.EnergySaverStatusChanged += (_, _) => Post();
            PowerManager.RemainingChargePercentChanged += (_, _) => Post();
            PowerManager.PowerSupplyStatusChanged += (_, _) => Post();
            PowerManager.BatteryStatusChanged += (_, _) => Post();
        }
        catch { }
        Prefs.Changed += k => { if (k == "lowImpactMode") Post(); };
        Update();
    }

    static void Post() => ui?.TryEnqueue(Update);

    public static void Update()
    {
        double? battery = null;
        bool charging = true;
        try
        {
            if (PowerManager.BatteryStatus != BatteryStatus.NotPresent)
            {
                battery = PowerManager.RemainingChargePercent;
                charging = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
            }
        }
        catch { }
        bool saver = false;
        try { saver = PowerManager.EnergySaverStatus == EnergySaverStatus.On; } catch { }
        var now = UpdateLogic.LowImpact(Prefs.Get("lowImpactMode", "auto"), saver, battery, charging);
        if (applied == now) return;   // nothing changed since last time
        applied = now;
        Prefs.Set("lowImpact", now);
        try { SettingsNative.SetEfficiencyMode(now); } catch { }
        App.Live.ApplyInterval();
        Changed?.Invoke();
    }
}
