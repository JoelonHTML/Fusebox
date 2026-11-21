using System.Net.NetworkInformation;
using Fusebox.Core.Network;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace Fusebox.Platform.Network;

/// <summary>Windows-only facts for the Connection section: network profile, VPN, encrypted DNS, metered.</summary>
public sealed record WinNetExtras(int? ProfileCategory, bool Vpn, bool? EncryptedDns, bool Metered);

/// <summary>Read on demand (details open, speed test), never polled.</summary>
public static class WinNet
{
    /// <summary>Metered, mobile data, roaming or near the data limit: the speed test asks first. Unknown = not metered.</summary>
    public static bool IsMetered()
    {
        try
        {
            var c = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return c != null && (c.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || c.Roaming || c.OverDataLimit || c.ApproachingDataLimit);
        }
        catch { return false; }
    }

    public static WinNetExtras Read(LocalNet? net)
    {
        int? category = null;
        try
        {
            var name = NetworkInformation.GetInternetConnectionProfile()?.ProfileName;   // only used to find the profile, never shown
            using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles");
            if (name != null && profiles != null)
                foreach (var sub in profiles.GetSubKeyNames())
                {
                    using var k = profiles.OpenSubKey(sub);
                    if (k?.GetValue("ProfileName") as string == name && k.GetValue("Category") is int cat) { category = cat; break; }
                }
        }
        catch { }
        bool vpn = false;
        try
        {
            vpn = NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up && WinExtras.IsVpn(n.NetworkInterfaceType.ToString(), n.Name + " " + n.Description));
        }
        catch { }
        return new(category, vpn, net == null ? null : EncryptedDns(net), IsMetered());
    }

    /// <summary>On when this adapter's DNS server has an encrypted (DoH) setting, or Windows upgrades well-known servers by itself. null = no DNS server known.</summary>
    static bool? EncryptedDns(LocalNet net)
    {
        if (net.Dns.Length == 0) return null;
        try
        {
            foreach (var dns in net.Dns)
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\{net.Id}\DohInterfaceSettings\Doh\{dns}");
                if (k != null) return true;
            }
            using var p = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters");
            bool auto = p?.GetValue("EnableAutoDoh") is int v && v == 2;
            return auto && net.Dns.Any(WinExtras.AutoDohServers.Contains);
        }
        catch { return null; }
    }
}
