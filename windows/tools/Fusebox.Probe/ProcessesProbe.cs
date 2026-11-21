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
        o["installedApps"] = InstalledApps.All().Count;
    }
}
