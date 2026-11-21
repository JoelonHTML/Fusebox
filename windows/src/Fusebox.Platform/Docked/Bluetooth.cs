using Fusebox.Core.Docked;
using static Fusebox.Platform.Docked.DockedNative;

namespace Fusebox.Platform.Docked;

/// <summary>
/// Paired Bluetooth devices from PnP (BTHENUM classic, BTHLE low energy) with the battery Windows Settings shows.
/// Kind from the class-of-device property (name as fallback). Not done: BLE GATT Battery Service (WinRT, async), left/right/case batteries; only the PnP battery property is read.
/// </summary>
internal static unsafe class Bluetooth
{
    /// <summary>null = no Bluetooth radio (hide the section).</summary>
    public static List<BtDevice>? Read()
    {
        bool radio = false;
        var list = new List<BtDevice>();
        Guid btClass = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
        DevProps.Each(&btClass, null, DIGCF_PRESENT, (set, d) =>
        {
            var id = DevProps.InstanceId(d.DevInst);
            if (!id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) { radio = true; return; }   // the radio itself sits on USB/PCI
            if (!id.Contains("\\DEV_", StringComparison.OrdinalIgnoreCase)) return;                      // skip per-service nodes
            var name = DevProps.String(set, d, DevProps.FriendlyName) ?? DevProps.String(set, d, DevProps.DeviceDesc);
            if (name == null) return;
            var conn = DevProps.Bytes(set, d, DevProps.BtIsConnected);
            var cod = DevProps.UInt(set, d, DevProps.BtClassOfDevice);
            list.Add(new BtDevice(name, conn is { Length: >= 1 } && conn[0] != 0, BtBattery.Parse(DevProps.Bytes(set, d, DevProps.BtBattery)), BtKind.From(cod, name)));
        });
        if (!radio && list.Count == 0) return null;
        return list.OrderByDescending(x => x.Connected).ThenBy(x => x.Name).ToList();
    }
}
