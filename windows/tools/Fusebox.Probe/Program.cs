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
};
Console.WriteLine(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
var failures = new List<string>();
if (LiveSampler.IfRowSize != 1352) failures.Add($"MIB_IF_ROW2 size {LiveSampler.IfRowSize}");
if (double.IsNaN(s.Cpu) || s.Cores.Length == 0) failures.Add("cpu");
if (!(s.RamTotal > 0)) failures.Add("ram");
if (double.IsNaN(s.NetIn)) failures.Add("net");
if (failures.Count > 0) { Console.Error.WriteLine("FAILED: " + string.Join(", ", failures)); return 1; }
return 0;
