using static Fusebox.Platform.Docked.DockedNative;

namespace Fusebox.Platform.Docked;

/// <summary>SetupAPI device enumeration + property helpers (shared by USB and Bluetooth).</summary>
internal static unsafe class DevProps
{
    public static readonly DEVPROPKEY DeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    public static readonly DEVPROPKEY Manufacturer = new("a45c254e-df1c-4efd-8020-67d146a850e0", 13);
    public static readonly DEVPROPKEY FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    public static readonly DEVPROPKEY Address = new("a45c254e-df1c-4efd-8020-67d146a850e0", 30);
    public static readonly DEVPROPKEY BusReportedDesc = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
    public static readonly DEVPROPKEY BtBattery = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    public static readonly DEVPROPKEY BtIsConnected = new("83DA6326-97A6-4088-9453-A1923F573B29", 15);

    /// <summary>Calls action for every present device in the set.</summary>
    public static void Each(Guid* classGuid, string? enumerator, uint flags, Action<IntPtr, SP_DEVINFO_DATA> action)
    {
        var set = SetupDiGetClassDevs(classGuid, enumerator, IntPtr.Zero, flags);
        if (set == InvalidHandle || set == IntPtr.Zero) return;
        try
        {
            for (uint i = 0; ; i++)
            {
                SP_DEVINFO_DATA d = default;
                d.CbSize = (uint)sizeof(SP_DEVINFO_DATA);
                if (!SetupDiEnumDeviceInfo(set, i, &d)) break;
                action(set, d);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static string InstanceId(uint devInst)
    {
        char* b = stackalloc char[200];
        return CM_Get_Device_ID(devInst, b, 200, 0) == 0 ? new string(b) : "";
    }

    public static byte[]? Bytes(IntPtr set, SP_DEVINFO_DATA d, DEVPROPKEY key)
    {
        byte* b = stackalloc byte[512];
        uint type, need;
        if (!SetupDiGetDeviceProperty(set, &d, &key, &type, b, 512, &need, 0)) return null;
        return new ReadOnlySpan<byte>(b, (int)Math.Min(need, 512)).ToArray();
    }

    public static string? String(IntPtr set, SP_DEVINFO_DATA d, DEVPROPKEY key)
    {
        var b = Bytes(set, d, key);
        if (b == null || b.Length < 2) return null;
        var s = System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');
        return s.Length == 0 ? null : s;
    }

    public static uint? UInt(IntPtr set, SP_DEVINFO_DATA d, DEVPROPKEY key)
    {
        var b = Bytes(set, d, key);
        return b is { Length: >= 4 } ? BitConverter.ToUInt32(b, 0) : null;
    }
}
