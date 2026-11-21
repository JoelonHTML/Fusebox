using Fusebox.Core.Checkup;

namespace Fusebox.Platform;

/// <summary>
/// Things that need administrator rights (SSD health, powercfg /requests, …). Fusebox itself runs as a normal user and never elevates
/// its own (user-writable) exe: the task is a PowerShell script (<see cref="ElevatedScripts"/>) run elevated by the system powershell.exe.
/// </summary>
public static class Elevated
{
    public static string PowerShellExe => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>Runs one task elevated (UAC prompt) and waits; null when the user said no to UAC or it failed.</summary>
    public static async Task<string?> RequestAsync(string name)
    {
        // Fresh unguessable folder; the empty output file is made here, unelevated, so the elevated script only writes into it.
        var dir = Path.Combine(Path.GetTempPath(), "fusebox-" + Guid.NewGuid().ToString("N"));
        var outFile = Path.Combine(dir, "result.txt");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(outFile, []);
            if (ElevatedScripts.Script(name, outFile) is not { } script) return null;
            var psi = new System.Diagnostics.ProcessStartInfo(PowerShellExe,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-EncodedCommand", ElevatedScripts.Encode(script)])
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            await p.WaitForExitAsync();
            var text = await File.ReadAllTextAsync(outFile);
            return text.Length > 0 ? text : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // UAC declined
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
