namespace Fusebox.Platform;

/// <summary>
/// Things that need administrator rights (SSD health, powercfg /requests, …). Fusebox itself runs as a normal user;
/// for one of these it starts itself once elevated: <c>Fusebox.exe --elevated &lt;task&gt; &lt;outFile&gt;</c> (UAC prompt),
/// the task writes its result (JSON text) to outFile and the process exits. Register tasks in <see cref="Tasks"/>.
/// </summary>
public static class Elevated
{
    public static readonly Dictionary<string, Func<string>> Tasks = new();

    /// <summary>Called from Program.Main before any UI. Returns the exit code.</summary>
    public static int RunTask(string name, string outFile)
    {
        if (!Tasks.TryGetValue(name, out var task)) return 2;
        try { File.WriteAllText(outFile, task()); return 0; }
        catch (Exception e) { File.WriteAllText(outFile, "{\"error\":" + System.Text.Json.JsonSerializer.Serialize(e.Message, ElevatedJson.Default.String) + "}"); return 1; }
    }

    /// <summary>Starts this exe elevated for one task and waits; null when the user said no to UAC or it failed.</summary>
    public static async Task<string?> RequestAsync(string name)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"fusebox-{name}-{Guid.NewGuid():N}.json");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, ["--elevated", name, outFile])
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            await p.WaitForExitAsync();
            return File.Exists(outFile) ? await File.ReadAllTextAsync(outFile) : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // UAC declined
        finally { try { File.Delete(outFile); } catch { } }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal partial class ElevatedJson : System.Text.Json.Serialization.JsonSerializerContext;
