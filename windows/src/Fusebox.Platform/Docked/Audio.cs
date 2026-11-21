using Fusebox.Core.Docked;
using static Fusebox.Platform.Docked.DockedNative;

namespace Fusebox.Platform.Docked;

/// <summary>Core Audio endpoints through raw vtable calls (no COM source generator needed, AOT-safe).</summary>
internal static unsafe class Audio
{
    // vtable slots: IUnknown 0-2; IMMDeviceEnumerator 3 EnumAudioEndpoints, 4 GetDefaultAudioEndpoint;
    // IMMDeviceCollection 3 GetCount, 4 Item; IMMDevice 4 OpenPropertyStore, 5 GetId, 6 GetState; IPropertyStore 5 GetValue
    static void** Vt(void* o) => *(void***)o;
    static void Release(void* o) { if (o != null) ((delegate* unmanaged[Stdcall]<void*, uint>)Vt(o)[2])(o); }

    public static List<AudioEndpoint> Read()
    {
        var res = new List<AudioEndpoint>();
        int hr = CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/);
        bool uninit = hr >= 0;   // S_OK or S_FALSE; RPC_E_CHANGED_MODE: use the thread's existing apartment, don't uninitialize
        try
        {
            Guid clsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E"), iid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
            void* en = null;
            if (CoCreateInstance(&clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, &iid, &en) < 0 || en == null) return res;
            try
            {
                for (uint flow = 0; flow < 2; flow++)   // 0 render, 1 capture
                {
                    string? defId = DefaultId(en, flow);
                    void* col = null;
                    if (((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)Vt(en)[3])(en, flow, 1 | 8 /*ACTIVE|UNPLUGGED*/, &col) < 0 || col == null) continue;
                    try
                    {
                        uint n = 0;
                        ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Vt(col)[3])(col, &n);
                        for (uint i = 0; i < n; i++)
                        {
                            void* dev = null;
                            if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vt(col)[4])(col, i, &dev) < 0 || dev == null) continue;
                            try { Add(dev, flow == 0, defId, res); } finally { Release(dev); }
                        }
                    }
                    finally { Release(col); }
                }
            }
            finally { Release(en); }
        }
        finally { if (uninit) CoUninitialize(); }
        return res;
    }

    static string? DefaultId(void* en, uint flow)
    {
        void* dev = null;
        if (((delegate* unmanaged[Stdcall]<void*, uint, uint, void**, int>)Vt(en)[4])(en, flow, 1 /*eMultimedia*/, &dev) < 0 || dev == null) return null;
        try { return Id(dev); } finally { Release(dev); }
    }

    static string? Id(void* dev)
    {
        char* p = null;
        if (((delegate* unmanaged[Stdcall]<void*, char**, int>)Vt(dev)[5])(dev, &p) < 0 || p == null) return null;
        var s = new string(p);
        CoTaskMemFree((IntPtr)p);
        return s;
    }

    static void Add(void* dev, bool output, string? defId, List<AudioEndpoint> res)
    {
        uint state = 0;
        ((delegate* unmanaged[Stdcall]<void*, uint*, int>)Vt(dev)[6])(dev, &state);
        void* ps = null;
        if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)Vt(dev)[4])(dev, 0 /*STGM_READ*/, &ps) < 0 || ps == null) return;
        try
        {
            // PROPVARIANT is 24 bytes on 64-bit: vt u16 @0, value @8
            byte* pv = stackalloc byte[24];
            Guid devGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0"), epGuid = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");
            DEVPROPKEY nameKey = new() { Fmtid = devGuid, Pid = 14 }, formKey = new() { Fmtid = epGuid, Pid = 0 };   // same layout as PROPERTYKEY
            string name = "Audio device";
            new Span<byte>(pv, 24).Clear();
            if (((delegate* unmanaged[Stdcall]<void*, DEVPROPKEY*, byte*, int>)Vt(ps)[5])(ps, &nameKey, pv) >= 0 && *(ushort*)pv == 31 && *(char**)(pv + 8) != null)
                name = new string(*(char**)(pv + 8));
            PropVariantClear(pv);
            uint form = 10;
            new Span<byte>(pv, 24).Clear();
            if (((delegate* unmanaged[Stdcall]<void*, DEVPROPKEY*, byte*, int>)Vt(ps)[5])(ps, &formKey, pv) >= 0 && *(ushort*)pv == 19) form = *(uint*)(pv + 8);
            PropVariantClear(pv);
            res.Add(new AudioEndpoint(name, AudioMap.FormFactor(form), output, Id(dev) is { } id && id == defId, state == 1));
        }
        finally { Release(ps); }
    }
}
