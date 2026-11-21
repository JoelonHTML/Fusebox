using System.Text.Json.Nodes;
using Fusebox.Core.Checkup;
using Fusebox.Platform.Checkup;

public static class CheckupProbe
{
    /// <summary>Fails only when the Event Log queries themselves error (every Windows PC has these channels); everything else is printed.</summary>
    public static void Run(JsonObject o, List<string> failures)
    {
        var c = new JsonObject();
        o["checkup"] = c;
        var now = DateTime.Now;
        try
        {
            var crashes = CrashAnalysis.Build(EventLogReader.Crashes(), now.AddDays(-30), now.AddDays(1));
            c["apps"] = crashes.Apps.Count; c["blueScreens"] = crashes.BlueScreens; c["unexpectedShutdowns"] = crashes.UnexpectedShutdowns; c["outOfMemory"] = crashes.OutOfMemory;
        }
        catch (Exception e) { failures.Add("checkup eventlog crashes: " + e.Message); }
        try
        {
            var events = EventLogReader.Sleep();
            var sleep = CheckupParsers.SummarizeSleep(events);
            c["sleeps"] = sleep.Sleeps; c["wakes"] = sleep.Wakes.Count; c["nightWakes"] = sleep.NightWakes;
            c["sleepSpans"] = SleepSpans.Build(events, []).Count;
        }
        catch (Exception e) { failures.Add("checkup eventlog sleep: " + e.Message); }

        // Best effort: print, never fail (CI runners are VMs without battery, SSD health or a Reliability history).
        try { var s = StartupScanner.Scan(); c["startupItems"] = s.Count; c["startupAtLogin"] = StartupParse.CountAtLogin(s); c["startupOrphans"] = s.Count(x => x.Orphan); c["startupKinds"] = string.Join(",", s.GroupBy(x => x.Kind).Select(g => $"{g.Key}={g.Count()}")); c["startupRunning"] = s.Count(x => x.Running); } catch (Exception e) { c["startupError"] = e.Message; }
        try { c["drives"] = CheckupReader.Drives().Count; } catch (Exception e) { c["drivesError"] = e.Message; }
        try { c["pendingReboot"] = CheckupReader.PendingReboot(); c["lastUpdate"] = CheckupReader.LastUpdate()?.ToString("yyyy-MM-dd"); } catch (Exception e) { c["rebootError"] = e.Message; }
        c["uptimeDays"] = Math.Round(CheckupReader.UptimeDays(), 2);
        try { c["windows"] = CheckupReader.WindowsVersion(); var id = CheckupReader.Identity(); c["identityLengths"] = $"{id.UserName.Length},{id.FullName.Length},{id.ComputerName.Length},{id.Domain.Length}"; } catch (Exception e) { c["identityError"] = e.Message; }
        try { var h = CheckupReader.ReliabilityHistory(); c["reliabilityPoints"] = h.Count; c["reliabilityLatest"] = h.LastOrDefault()?.Index; } catch (Exception e) { c["reliabilityError"] = e.Message; }
        try { c["physicalDisks"] = CheckupReader.Disks().Select(d => $"{d.MediaType}:{d.Health}").ToArray().Length; } catch (Exception e) { c["disksError"] = e.Message; }
        try { var b = CheckupReader.BatteryReport(); c["batteryReport"] = b == null ? "none" : $"{b.Value.Now?.Percent}% / history {b.Value.History.Count}"; } catch (Exception e) { c["batteryReportError"] = e.Message; }
        try { c["housekeepingNow"] = string.Join("; ", WindowsJobs.Busy(Environment.ProcessorCount)); } catch (Exception e) { c["housekeepingError"] = e.Message; }
        try
        {
            var plan = DiskScanner.Plan();
            c["diskRoots"] = plan.Count;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var r = DiskScanner.ScanAsync(plan.Take(6).ToList(), new Progress<(int, int, string)>(), cts.Token).GetAwaiter().GetResult();
            c["diskScanRows"] = r.Rows.Count; c["diskScanNotCounted"] = r.NotCounted.Count; c["diskScanTopMb"] = r.Rows.Count > 0 ? r.Rows[0].Bytes / 1_000_000 : 0;
        }
        catch (Exception e) { c["diskScanError"] = e.Message; }
        try
        {
            var r = SsdBench.RunAsync(_ => { }, CancellationToken.None).GetAwaiter().GetResult();
            c["ssdRead"] = Math.Round(r.ReadMBps); c["ssdWrite"] = Math.Round(r.WriteMBps);
        }
        catch (Exception e) { c["ssdBenchError"] = e.Message; }
    }
}
