using System.Text.Json.Nodes;
using Fusebox.Core.Checkup;
using Fusebox.Platform.Checkup;

public static class CheckupProbe
{
    /// <summary>Fails only when the Event Log query itself errors (every Windows PC has these channels).</summary>
    public static void Run(JsonObject o, List<string> failures)
    {
        var c = new JsonObject();
        o["checkup"] = c;
        var inputs = new CheckupInputs();
        try
        {
            var crashes = CheckupParsers.Summarize(EventLogReader.Crashes());
            inputs.Crashes = crashes;
            c["crashes"] = crashes.Total; c["appCrashes"] = crashes.AppCrashes; c["hangs"] = crashes.Hangs;
            c["blueScreens"] = crashes.BlueScreens; c["unexpectedShutdowns"] = crashes.UnexpectedShutdowns;
        }
        catch (Exception e) { failures.Add("checkup eventlog crashes: " + e.Message); }
        try
        {
            var sleep = CheckupParsers.SummarizeSleep(EventLogReader.Sleep());
            inputs.Sleep = sleep;
            c["sleeps"] = sleep.Sleeps; c["wakes"] = sleep.Wakes.Count; c["nightWakes"] = sleep.NightWakes;
        }
        catch (Exception e) { failures.Add("checkup eventlog sleep: " + e.Message); }

        // Best effort: print, never fail.
        try { var s = CheckupReader.StartupItems(); inputs.StartupEnabled = s.Count(x => x.Enabled); c["startupItems"] = s.Count; c["startupEnabled"] = inputs.StartupEnabled; } catch (Exception e) { c["startupError"] = e.Message; }
        try { inputs.Drives = CheckupReader.Drives(); c["drives"] = inputs.Drives.Count; } catch (Exception e) { c["drivesError"] = e.Message; }
        try { inputs.PendingReboot = CheckupReader.PendingReboot(); c["pendingReboot"] = inputs.PendingReboot; } catch (Exception e) { c["rebootError"] = e.Message; }
        inputs.UptimeDays = CheckupReader.UptimeDays(); c["uptimeDays"] = Math.Round(inputs.UptimeDays.Value, 2);
        try { var cp = CheckupReader.CommitPct(); inputs.CommitPct = double.IsNaN(cp) ? null : cp; c["commitPct"] = inputs.CommitPct; } catch (Exception e) { c["commitError"] = e.Message; }
        try { inputs.StabilityIndex = CheckupReader.StabilityIndex(); c["stabilityIndex"] = inputs.StabilityIndex; } catch (Exception e) { c["stabilityError"] = e.Message; }
        try { inputs.Disks = CheckupReader.Disks(); c["physicalDisks"] = inputs.Disks.Count; } catch (Exception e) { c["disksError"] = e.Message; }
        try { inputs.Battery = CheckupReader.Battery(); c["batteryPercent"] = inputs.Battery?.Percent; } catch (Exception e) { c["batteryError"] = e.Message; }

        var r = HealthScore.Evaluate(inputs);
        c["score"] = r.Score;
        c["categories"] = new JsonArray(r.Categories.Select(x => (JsonNode)JsonValue.Create($"{x.Id}={x.Score}")!).ToArray());
    }
}
