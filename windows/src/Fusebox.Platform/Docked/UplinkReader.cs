using System.Net.NetworkInformation;
using Fusebox.Core.Docked;
using Windows.Networking.Connectivity;

namespace Fusebox.Platform.Docked;

/// <summary>
/// Is the internet coming through a phone, a modem or a metered link? From the internet connection profile (cost, mobile broadband)
/// plus the adapter's description. Never reads the profile name: for Wi-Fi that is the network name.
/// </summary>
internal static class UplinkReader
{
    public static UplinkInfo? Read()
    {
        var p = NetworkInformation.GetInternetConnectionProfile();
        if (p == null) return null;
        var cost = p.GetConnectionCost();
        bool metered = cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.OverDataLimit || cost.Roaming;
        int iana = p.NetworkAdapter is { } a ? (int)a.IanaInterfaceType : 0;
        string desc = "";
        if (p.NetworkAdapter is { } ad)
        {
            var id = ad.NetworkAdapterId.ToString("B");
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                if (string.Equals(ni.Id, id, StringComparison.OrdinalIgnoreCase)) { desc = ni.Description; break; }
        }
        var kind = Uplink.Classify(p.IsWwanConnectionProfile, iana, desc, metered);
        return kind == null ? null : new UplinkInfo(kind, desc.Length > 0 ? desc : null, metered);
    }
}
