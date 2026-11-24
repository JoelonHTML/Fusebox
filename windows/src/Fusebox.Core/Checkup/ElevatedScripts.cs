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

    const string Approved = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static string? Body(string task) => task switch { "ssd" => Ssd, "requests" => Requests, _ => null };

    /// <summary>
    /// PowerShell single-quoted literal. Besides ' every Unicode single-quote look-alike (U+2018–201B) ends a literal in PowerShell,
    /// so they are doubled too: a name read from the registry or Task Scheduler can never break out of the quotes.
    /// </summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder("'");
        foreach (var ch in s)
        {
            if (ch is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(ch);
            if (char.IsControl(ch)) continue;
            sb.Append(ch);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>
    /// The switch for an item that needs administrator rights (HKLM Run, common Startup folder, tasks, services). Same StartupApproved bytes
    /// as Task Manager; a task is disabled, a service set to manual (on = automatic). Output "ok" or "error: …". Null = not switchable.
    /// </summary>
    public static string? Toggle(StartupKind kind, StartupScope scope, string id, bool enable)
    {
        string body;
        switch (kind)
        {
            case StartupKind.Run or StartupKind.Folder when scope != StartupScope.User:
                var leaf = kind == StartupKind.Folder ? "StartupFolder" : scope == StartupScope.Machine32 ? "Run32" : "Run";
                var bytes = string.Join(',', CheckupParsers.StartupApprovedValue(enable, DateTime.UtcNow));
                body = $"$k={Quote(Approved + leaf)};if(-not(Test-Path -LiteralPath $k)){{New-Item -Path $k -Force | Out-Null}};"
                     + $"Set-ItemProperty -LiteralPath $k -Name {Quote(id)} -Type Binary -Value ([byte[]]@({bytes}))";
                break;
            case StartupKind.Task:
                int slash = id.LastIndexOf('\\');
                var path = slash >= 0 ? id[..(slash + 1)] : @"\";
                var name = id[(slash + 1)..];
                body = $"{(enable ? "Enable" : "Disable")}-ScheduledTask -TaskPath {Quote(path)} -TaskName {Quote(name)} | Out-Null";
                break;
            case StartupKind.Service:
                body = $"Set-Service -Name {Quote(id)} -StartupType {(enable ? "Automatic" : "Manual")}";
                break;
            default: return null;
        }
        return "try{" + body + ";'ok'}catch{'error: '+$_.Exception.Message}";
    }

    /// <summary>Full script for a task, or null for an unknown one.</summary>
    public static string? Script(string task, string outFile) => Body(task) is not { } body ? null : Wrap(body, outFile);

    /// <summary>Wraps a body so its output ends in the pre-created result file.</summary>
    public static string Wrap(string body, string outFile) =>
        "$env:PSModulePath = \"$env:SystemRoot\\System32\\WindowsPowerShell\\v1.0\\Modules\"\n$ProgressPreference='SilentlyContinue'\n"
        + "$(" + body + ") | Out-File -LiteralPath " + Quote(outFile) + " -Encoding utf8";

    /// <summary>Base64 of UTF-16LE, for -EncodedCommand.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
