using Fusebox.Core;
using Microsoft.Win32;

namespace Fusebox.Platform;

/// <summary>GPU via PDH, like Task Manager: sum the engines of one type over all processes, GPU % = busiest type.</summary>
internal sealed class GpuReader(PdhQuery q)
{
    readonly int engines = q.Add(@"\GPU Engine(*)\Utilization Percentage");
    readonly int dedicated = q.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
    readonly int shared = q.Add(@"\GPU Adapter Memory(*)\Shared Usage");

    public void Read(LiveSample s)
    {
        if (engines >= 0)
        {
            var byType = new Dictionary<string, double>();
            foreach (var (name, v) in q.Values(engines))
            {
                int i = name.LastIndexOf("engtype_", StringComparison.Ordinal);
                if (i < 0) continue;
                var type = name[(i + 8)..];
                byType[type] = byType.GetValueOrDefault(type) + v;
            }
            if (byType.Count > 0)
            {
                s.GpuEngines = byType.Select(kv => (kv.Key, Math.Min(kv.Value, 100))).OrderByDescending(e => e.Item2).ToArray();
                s.Gpu = s.GpuEngines[0].Percent;
            }
        }
        s.GpuMemDedicated = Sum(dedicated);
        s.GpuMemShared = Sum(shared);
    }

    double Sum(int counter)
    {
        var v = q.Values(counter);
        return v.Count == 0 ? double.NaN : v.Sum(x => x.Value);
    }

    /// <summary>First real display adapter's name from the registry (read once).</summary>
    public static string Name()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11cd-b911-00a0c9c1ed93}");
            if (cls == null) return "";
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!int.TryParse(sub, out _)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string d && !d.StartsWith("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) return d;
            }
        }
        catch { }
        return "";
    }
}
