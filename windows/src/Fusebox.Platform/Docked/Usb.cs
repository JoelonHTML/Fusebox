using Fusebox.Core.Docked;
using static Fusebox.Platform.Docked.DockedNative;

namespace Fusebox.Platform.Docked;

/// <summary>USB tree: devices from SetupAPI (parent via CM_Get_Parent), speed per port from the hub's IOCTLs.</summary>
internal static unsafe class Usb
{
    // CTL_CODE(FILE_DEVICE_USB 0x22, fn, METHOD_BUFFERED, FILE_ANY_ACCESS) = 0x220000 | fn << 2
    const uint IoctlConnEx = 0x220448;      // fn 274 USB_GET_NODE_CONNECTION_INFORMATION_EX
    const uint IoctlConnExV2 = 0x22045C;    // fn 279 USB_GET_NODE_CONNECTION_INFORMATION_EX_V2
    const uint IoctlPortProps = 0x220458;   // fn 278 USB_GET_PORT_CONNECTOR_PROPERTIES

    sealed class Node
    {
        public uint Parent; public string Name = "", Id = ""; public string? Maker; public uint Port; public bool RootHub;
        public List<uint> Kids = new();
    }

    public static List<UsbNode> Read()
    {
        // 1. hub interface paths by owning devnode
        var hubs = new Dictionary<uint, string>();
        Guid hubGuid = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
        var hset = SetupDiGetClassDevs(&hubGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hset != InvalidHandle && hset != IntPtr.Zero)
        {
            byte* detail = stackalloc byte[1024];
            try
            {
                for (uint i = 0; ; i++)
                {
                    SP_DEVICE_INTERFACE_DATA ifd = default;
                    ifd.CbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA);
                    if (!SetupDiEnumDeviceInterfaces(hset, null, &hubGuid, i, &ifd)) break;
                    *(uint*)detail = (uint)(IntPtr.Size == 8 ? 8 : 6);   // cbSize of the fixed part
                    SP_DEVINFO_DATA di = default;
                    di.CbSize = (uint)sizeof(SP_DEVINFO_DATA);
                    if (SetupDiGetDeviceInterfaceDetail(hset, &ifd, detail, 1024, null, &di))
                        hubs[di.DevInst] = new string((char*)(detail + 4));
                }
            }
            finally { SetupDiDestroyDeviceInfoList(hset); }
        }

        // 2. all present USB devnodes (skip composite interfaces "&MI_")
        var nodes = new Dictionary<uint, Node>();
        DevProps.Each(null, "USB", DIGCF_PRESENT | DIGCF_ALLCLASSES, (set, d) =>
        {
            var id = DevProps.InstanceId(d.DevInst);
            if (!id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) return;
            uint parent = 0;
            CM_Get_Parent(&parent, d.DevInst, 0);
            nodes[d.DevInst] = new Node
            {
                Parent = parent, Id = id, RootHub = id.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase),
                Name = DevProps.String(set, d, DevProps.BusReportedDesc) ?? DevProps.String(set, d, DevProps.FriendlyName) ?? DevProps.String(set, d, DevProps.DeviceDesc) ?? "USB device",
                Maker = DevProps.String(set, d, DevProps.Manufacturer),
                Port = DevProps.UInt(set, d, DevProps.Address) ?? 0,
            };
        });
        foreach (var (k, n) in nodes) if (nodes.TryGetValue(n.Parent, out var p)) p.Kids.Add(k);

        // 3. flatten: root hubs are not shown, their children start at depth 0
        var res = new List<UsbNode>();
        var handles = new Dictionary<string, IntPtr>();
        void Walk(uint k, int depth)
        {
            var n = nodes[k];
            if (!n.RootHub)
            {
                string? now = null, max = null; bool slow = false; bool? typeC = null;
                if (hubs.TryGetValue(n.Parent, out var hubPath)) Port(hubPath, n.Port, handles, out now, out max, out slow, out typeC);
                var maker = n.Maker is { Length: > 0 } m && !m.StartsWith('(') && !n.Name.Contains(m, StringComparison.OrdinalIgnoreCase) ? m : null;
                res.Add(new UsbNode(n.Name, maker, depth, n.Kids.Count > 0 || hubs.ContainsKey(k), now, max, slow, typeC));
            }
            foreach (var c in n.Kids.OrderBy(c => nodes[c].Port)) Walk(c, n.RootHub ? 0 : depth + 1);
        }
        try
        {
            foreach (var (k, n) in nodes.OrderBy(x => x.Value.Id))
                if (!nodes.ContainsKey(n.Parent)) Walk(k, 0);
        }
        finally { foreach (var h in handles.Values) if (h != InvalidHandle) CloseHandle(h); }
        return res;
    }

    static void Port(string hubPath, uint port, Dictionary<string, IntPtr> handles, out string? now, out string? max, out bool slow, out bool? typeC)
    {
        now = max = null; slow = false; typeC = null;
        if (!handles.TryGetValue(hubPath, out var h))
            handles[hubPath] = h = CreateFile(hubPath, 0xC0000000 /*GENERIC_READ|WRITE*/, 3, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (h == InvalidHandle || port == 0) return;
        uint ret;

        // USB_NODE_CONNECTION_INFORMATION_EX (Pack=1): index u32 @0, device descriptor 18 bytes @4, config @22, Speed @23, isHub @24, address u16 @25, pipes u32 @27, status u32 @31 (1 = connected)
        byte* ex = stackalloc byte[64];
        new Span<byte>(ex, 64).Clear();
        *(uint*)ex = port;
        if (!DeviceIoControl(h, IoctlConnEx, ex, 64, ex, 64, &ret, IntPtr.Zero) || *(uint*)(ex + 31) != 1) return;
        int speed = ex[23];

        // _V2: index u32 @0, Length u32 @4 (=16), SupportedUsbProtocols u32 @8, Flags u32 @12. Older stacks fail: then speed alone decides.
        byte* v2 = stackalloc byte[16];
        new Span<byte>(v2, 16).Clear();
        *(uint*)v2 = port; *(uint*)(v2 + 4) = 16;
        uint protocols = 0, flags = 0;
        if (DeviceIoControl(h, IoctlConnExV2, v2, 16, v2, 16, &ret, IntPtr.Zero)) { protocols = *(uint*)(v2 + 8); flags = *(uint*)(v2 + 12); }
        (now, max, slow) = UsbSpeed.Describe(speed, flags, protocols);

        // USB_PORT_CONNECTOR_PROPERTIES: index u32 @0, ActualLength u32 @4, UsbPortProperties u32 @8 (bit3 = connector is Type-C)
        byte* pp = stackalloc byte[128];
        new Span<byte>(pp, 128).Clear();
        *(uint*)pp = port;
        if (DeviceIoControl(h, IoctlPortProps, pp, 128, pp, 128, &ret, IntPtr.Zero)) typeC = (*(uint*)(pp + 8) & 8) != 0;
    }
}
