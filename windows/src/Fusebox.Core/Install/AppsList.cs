namespace Fusebox.Core.Install;

/// <summary>Pure logic for the Windows "Installed apps" entry (HKCU\...\Uninstall\Fusebox) and for the safe uninstall.</summary>
public static class AppsList
{
    public const string Url = "https://github.com/JoelonHTML/Fusebox";

    static string Norm(string p) => p.TrimEnd('\\', '/');

    /// <summary>The one folder Fusebox may register for / delete: %LOCALAPPDATA%\Programs\Fusebox.</summary>
    public static string InstallDir(string localAppData) => Norm(localAppData) + "\\Programs\\Fusebox";

    /// <summary>True only when the running folder is exactly the install folder (not Downloads, not a dev build).</summary>
    public static bool IsInstalledCopy(string baseDir, string localAppData) =>
        string.Equals(Norm(baseDir), InstallDir(localAppData), StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder may be deleted: exactly the install folder and it holds Fusebox.exe.</summary>
    public static bool SafeToDelete(string dir, string localAppData, bool hasExe) => hasExe && IsInstalledCopy(dir, localAppData);

    /// <summary>Registry values for the Uninstall key. Ints are DWORDs (NoModify, NoRepair, EstimatedSize), the rest strings.</summary>
    public static Dictionary<string, object> Values(string exe, string dir, string version, long sizeBytes, DateTime today) => new()
    {
        ["DisplayName"] = "Fusebox",
        ["DisplayVersion"] = version,
        ["Publisher"] = "ClaudIA Software & Development",
        ["DisplayIcon"] = exe,
        ["InstallLocation"] = dir,
        ["InstallDate"] = today.ToString("yyyyMMdd"),
        ["EstimatedSize"] = (int)Math.Min(int.MaxValue, (sizeBytes + 1023) / 1024),
        ["URLInfoAbout"] = Url,
        ["HelpLink"] = Url,
        ["NoModify"] = 1,
        ["NoRepair"] = 1,
        ["UninstallString"] = $"\"{exe}\" --uninstall",
        ["QuietUninstallString"] = $"\"{exe}\" --uninstall --quiet",
    };

    /// <summary>Rewrite only when nothing is there yet, or version / size / exe path changed (size within 1 MB counts as the same).</summary>
    public static bool NeedsWrite(string? oldVersion, long? oldSizeKb, string? oldExe, string version, long newSizeKb, string exe) =>
        oldVersion != version || oldExe != exe || oldSizeKb is null || Math.Abs(oldSizeKb.Value - newSizeKb) > 1024;
}
