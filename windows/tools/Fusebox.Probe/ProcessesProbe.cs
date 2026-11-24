using System.Text.Json.Nodes;
using Fusebox.Core.Processes;
using Fusebox.Platform;

public static class ProcessesProbe
{
    public static void Run(JsonObject o, List<string> failures)
    {
        using var sampler = new ProcessSampler();
        sampler.Sample();
        Thread.Sleep(1000);
        var s = sampler.Sample();
        var top = s.Groups.OrderByDescending(g => g.Cpu).Take(3)
            .Select(g => new JsonObject { ["name"] = g.Name, ["kind"] = g.Kind.ToString(), ["cpu"] = g.Cpu, ["gpu"] = g.Gpu, ["ram"] = g.Ram, ["pids"] = g.Pids.Count, ["path"] = g.ExePath });
        o["processes"] = new JsonObject { ["count"] = s.ProcessCount, ["groups"] = s.Groups.Count, ["ownCpu"] = s.OwnCpu, ["ownRam"] = s.OwnRam, ["top3"] = new JsonArray(top.ToArray()) };
        if (s.ProcessCount < 10) failures.Add($"processes count {s.ProcessCount}");
        if (!s.Groups.Any(g => g.Kind == AppKind.Windows)) failures.Add("processes: no Windows group (offsets wrong?)");
        if (!(s.OwnRam > 0)) failures.Add("processes: own process not found");
        var win = s.Groups.First(g => g.Kind == AppKind.Windows);
        o["processes"]!["windowsItself"] = new JsonObject { ["cpu"] = win.Cpu, ["ram"] = win.Ram, ["pids"] = win.Pids.Count };
        if (!(win.Ram > 0)) failures.Add("processes: Windows itself has no memory");

        // I/O counters (offsets 232/240 of SYSTEM_PROCESS_INFORMATION): the machine has read something since boot.
        var all = new ProcessReader().Read();
        long read = all.Sum(p => p.ReadBytes), write = all.Sum(p => p.WriteBytes);
        o["processes"]!["ioBytes"] = new JsonObject { ["read"] = read, ["write"] = write };
        if (read <= 0 || write <= 0) failures.Add("processes: per-process I/O counters are zero (offsets wrong?)");

        var apps = InstalledApps.All();
        o["installedApps"] = apps.Count;
        o["installedWithPath"] = apps.Count(a => a.Path != null);

        // About card data for a file every PC has: this probe itself.
        if (Environment.ProcessPath is { } exe)
        {
            var f = AppInfo.Read(exe, "Fusebox.Probe");
            o["appInfo"] = new JsonObject { ["version"] = f.Version, ["machine"] = f.Machine, ["emulated"] = f.Emulated, ["sizeBytes"] = f.SizeBytes, ["sizeIsMinimum"] = f.SizeIsMinimum,
                ["location"] = f.Location, ["signedBy"] = f.SignedBy, ["installed"] = f.Installed?.ToString("yyyy-MM-dd"), ["opensAtLogin"] = f.OpensAtLogin };
            if (f.Machine == null) failures.Add("appInfo: PE machine type not read");
        }

        // Efficiency mode (EcoQoS) on a process we own.
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ping.exe", "-n 6 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false });
        if (child != null)
        {
            var pids = new[] { child.Id };
            int on = ProcessControl.SetEfficiency(pids, true);
            bool isOn = ProcessControl.IsEfficient(pids);
            ProcessControl.SetEfficiency(pids, false);
            bool isOff = ProcessControl.IsEfficient(pids);
            o["efficiency"] = new JsonObject { ["set"] = on, ["readOn"] = isOn, ["readOffAfter"] = isOff };
            if (on == 0 || !isOn) failures.Add("efficiency mode: could not switch EcoQoS on for an own process");
            try { child.Kill(); } catch { }
        }
    }
}
