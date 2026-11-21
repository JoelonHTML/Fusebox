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
    const uint IoctlDescriptor = 0x220410;  // fn 260 USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION

    /// <summary>What the hub tells about one port (all optional: older stacks and some hubs answer only part).</summary>
    struct PortInfo
    {
        public string? Now, Max; public bool Slow; public bool? TypeC, Connectable;
        public long Bps, MaxBps; public int? Vid, Pid, Ma; public int Class;
    }

    sealed class Node
    {
        public uint Parent; public string Name = "", Id = ""; public string? Maker; public uint Port; public bool RootHub;
        public List<uint> Kids = new();
        public List<(string, string)> Raw = new();
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
            // Plain properties for "All values". No instance id, no serial: the page filters by key name as well.
            var raw = nodes[d.DevInst].Raw;
            void Add(string key, string? v) { if (!string.IsNullOrEmpty(v)) raw.Add((key, v)); }
            Add("Class", DevProps.String(set, d, DevProps.ClassName));
            Add("Driver", DevProps.String(set, d, DevProps.Service));
            Add("Location", DevProps.String(set, d, DevProps.LocationInfo));
            Add("Hardware ID", DevProps.String(set, d, DevProps.HardwareIds)?.Split('\0')[0]);
        });
        foreach (var (k, n) in nodes) if (nodes.TryGetValue(n.Parent, out var p)) p.Kids.Add(k);

        // 3. flatten: root hubs are not shown, their children start at depth 0. Path = port numbers from the root hub.
        var res = new List<UsbNode>();
        var handles = new Dictionary<string, IntPtr>();
        void Walk(uint k, int depth, int root, string path)
        {
            var n = nodes[k];
            if (!n.RootHub)
            {
                var pi = hubs.TryGetValue(n.Parent, out var hubPath) ? Port(hubPath, n.Port, handles) : default;
                var maker = n.Maker is { Length: > 0 } m && !m.StartsWith('(') && !n.Name.Contains(m, StringComparison.OrdinalIgnoreCase) ? m : null;
                bool hub = n.Kids.Count > 0 || hubs.ContainsKey(k) || pi.Class == 9;
                res.Add(new UsbNode(n.Name, maker, depth, hub, pi.Now, pi.Max, pi.Slow, pi.TypeC, pi.Bps, pi.MaxBps, pi.Vid, pi.Pid, pi.Class == 0 ? -1 : pi.Class,
                    pi.Ma, pi.Connectable, path, root, n.Raw));
            }
            foreach (var c in n.Kids.OrderBy(c => nodes[c].Port)) Walk(c, n.RootHub ? 0 : depth + 1, root, n.RootHub ? $"{root}/{nodes[c].Port}" : $"{path}/{nodes[c].Port}");
        }
        try
        {
            int rootIndex = 0;
            foreach (var (k, n) in nodes.OrderBy(x => x.Value.Id))
                if (!nodes.ContainsKey(n.Parent)) Walk(k, 0, rootIndex++, "");
        }
        finally { foreach (var h in handles.Values) if (h != InvalidHandle) CloseHandle(h); }
        return res;
    }

    static PortInfo Port(string hubPath, uint port, Dictionary<string, IntPtr> handles)
    {
        var r = new PortInfo { Class = -1 };
        if (!handles.TryGetValue(hubPath, out var h))
            handles[hubPath] = h = CreateFile(hubPath, 0xC0000000 /*GENERIC_READ|WRITE*/, 3, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (h == InvalidHandle || port == 0) return r;
        uint ret;

        // USB_NODE_CONNECTION_INFORMATION_EX (Pack=1): index u32 @0, device descriptor 18 bytes @4 (class @8, idVendor @12, idProduct @14), config @22, Speed @23, isHub @24, address u16 @25, pipes u32 @27, status u32 @31 (1 = connected)
        byte* ex = stackalloc byte[64];
        new Span<byte>(ex, 64).Clear();
        *(uint*)ex = port;
        if (!DeviceIoControl(h, IoctlConnEx, ex, 64, ex, 64, &ret, IntPtr.Zero) || *(uint*)(ex + 31) != 1) return r;
        int speed = ex[23];
        r.Class = ex[8]; r.Vid = *(ushort*)(ex + 12); r.Pid = *(ushort*)(ex + 14);
        if (ex[24] != 0) r.Class = 9;

        // _V2: index u32 @0, Length u32 @4 (=16), SupportedUsbProtocols u32 @8, Flags u32 @12. Older stacks fail: then speed alone decides.
        byte* v2 = stackalloc byte[16];
        new Span<byte>(v2, 16).Clear();
        *(uint*)v2 = port; *(uint*)(v2 + 4) = 16;
        uint protocols = 0, flags = 0;
        if (DeviceIoControl(h, IoctlConnExV2, v2, 16, v2, 16, &ret, IntPtr.Zero)) { protocols = *(uint*)(v2 + 8); flags = *(uint*)(v2 + 12); }
        (r.Now, r.Max, r.Slow) = UsbSpeed.Describe(speed, flags, protocols);
        var (nowRank, maxRank) = UsbSpeed.Ranks(speed, flags, protocols);
        r.Bps = UsbSpeed.Bps(nowRank); r.MaxBps = UsbSpeed.Bps(maxRank);

        // USB_PORT_CONNECTOR_PROPERTIES: index u32 @0, ActualLength u32 @4, UsbPortProperties u32 @8 (bit0 user connectable, bit3 connector is Type-C)
        byte* pp = stackalloc byte[128];
        new Span<byte>(pp, 128).Clear();
        *(uint*)pp = port;
        if (DeviceIoControl(h, IoctlPortProps, pp, 128, pp, 128, &ret, IntPtr.Zero))
        {
            uint props = *(uint*)(pp + 8);
            r.TypeC = (props & 8) != 0; r.Connectable = (props & 1) != 0;
        }

        // Configuration descriptor (9 bytes) through the hub: bMaxPower @8 = the power the device asks for, not what it draws now.
        // USB_DESCRIPTOR_REQUEST: index u32 @0, setup packet @4 (bmRequestType, bRequest, wValue u16, wIndex u16, wLength u16), data @12.
        byte* dr = stackalloc byte[21];
        new Span<byte>(dr, 21).Clear();
        *(uint*)dr = port; dr[4] = 0x80; dr[5] = 6; *(ushort*)(dr + 6) = 0x0200; *(ushort*)(dr + 10) = 9;
        if (DeviceIoControl(h, IoctlDescriptor, dr, 21, dr, 21, &ret, IntPtr.Zero) && ret >= 21 && dr[13] == 2)
            r.Ma = UsbSpeed.PowerMa(dr[12 + 8], speed);
        return r;
    }
}
