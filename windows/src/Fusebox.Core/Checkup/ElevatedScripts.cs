using System.Text;

namespace Fusebox.Core.Checkup;

/// <summary>
/// Admin-only one-off tasks as PowerShell scripts. They run elevated from the system powershell.exe, never from Fusebox's own
/// (user-writable) folder. Each script ends with its result written to a pre-created output file.
/// </summary>
public static class ElevatedScripts
{
    const string Ssd = "try{$d=@(Get-PhysicalDisk | Select-Object FriendlyName,MediaType,HealthStatus,Size);"
        + "$r=@(Get-PhysicalDisk | Get-StorageReliabilityCounter | Select-Object DeviceId,Wear,Temperature,ReadErrorsTotal,PowerOnHours);"
        + "[pscustomobject]@{disks=$d;rel=$r} | ConvertTo-Json -Depth 4}catch{[pscustomobject]@{error=$_.Exception.Message} | ConvertTo-Json}";

    const string Requests = "& \"$env:SystemRoot\\System32\\powercfg.exe\" /requests";

    public static string? Body(string task) => task switch { "ssd" => Ssd, "requests" => Requests, _ => null };

    /// <summary>PowerShell single-quoted literal.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Full script for a task, or null for an unknown one.</summary>
    public static string? Script(string task, string outFile) => Body(task) is not { } body ? null :
        "$env:PSModulePath = \"$env:SystemRoot\\System32\\WindowsPowerShell\\v1.0\\Modules\"\n$ProgressPreference='SilentlyContinue'\n"
        + "(" + body + ") | Out-File -LiteralPath " + Quote(outFile) + " -Encoding utf8";

    /// <summary>Base64 of UTF-16LE, for -EncodedCommand.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
