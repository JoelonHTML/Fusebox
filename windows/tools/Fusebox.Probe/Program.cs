using System.Text.Json;
using System.Text.Json.Nodes;
using Fusebox.Platform;

// CI self-test: read everything a few times on the real Windows runner and print JSON.
// Exit code 1 when a reader that must work on every PC returns nothing.
var live = new LiveSampler();
live.Sample();
Thread.Sleep(1000);
var s = live.Sample();
var o = new JsonObject
{
    ["cpu"] = s.Cpu, ["cores"] = s.Cores.Length, ["ramUsed"] = s.RamUsed, ["ramTotal"] = s.RamTotal,
    ["netIn"] = s.NetIn, ["netOut"] = s.NetOut,
    ["commitUsed"] = s.CommitUsed, ["commitLimit"] = s.CommitLimit,
    ["gpu"] = s.Gpu, ["gpuEngines"] = string.Join(", ", s.GpuEngines.Select(e => $"{e.Engine} {e.Percent:0}%")),
    ["gpuMemDedicated"] = s.GpuMemDedicated, ["gpuMemShared"] = s.GpuMemShared,
    ["diskRead"] = s.DiskRead, ["diskWrite"] = s.DiskWrite, ["diskFree"] = s.DiskFree, ["diskTotal"] = s.DiskTotal,
    ["battery"] = s.Battery, ["onAc"] = s.OnAc, ["charging"] = s.Charging, ["batteryMw"] = s.BatteryMw,
    ["batteryMinutes"] = s.BatteryMinutes, ["batteryDesign"] = s.BatteryDesign, ["batteryFull"] = s.BatteryFull, ["batteryCycles"] = s.BatteryCycles,
    ["cpuName"] = LiveSampler.Hardware.CpuName, ["gpuName"] = LiveSampler.Hardware.GpuName,
    ["pCores"] = LiveSampler.Hardware.PerformanceCores, ["eCores"] = LiveSampler.Hardware.EfficiencyCores,
};
var failures = new List<string>();
ProcessesProbe.Run(o, failures);
CheckupProbe.Run(o, failures);
DockedProbe.Run(o, failures);
NetworkProbe.Run(o, failures);
if (LiveSampler.IfRowSize != 1352) failures.Add($"MIB_IF_ROW2 size {LiveSampler.IfRowSize}");
if (double.IsNaN(s.Cpu) || s.Cores.Length == 0) failures.Add("cpu");
if (!(s.RamTotal > 0)) failures.Add("ram");
if (double.IsNaN(s.NetIn)) failures.Add("net");
if (double.IsNaN(s.DiskRead) || !(s.DiskTotal > 0)) failures.Add("disk");
if (LiveSampler.Hardware.CpuName.Length == 0 || LiveSampler.Hardware.PerformanceCores == 0) failures.Add("cpuinfo");
// GPU counters and battery are optional on CI runners (VM): printed above, not checked.
Console.WriteLine(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
if (failures.Count > 0) { Console.Error.WriteLine("FAILED: " + string.Join(", ", failures)); return 1; }
return 0;
