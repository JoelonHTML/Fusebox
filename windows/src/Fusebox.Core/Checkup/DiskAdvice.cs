using static Fusebox.Core.Strings;

namespace Fusebox.Core.Checkup;

public enum Verdict { Safe, Careful, Keep, Unknown }

/// <summary>One folder row of "What takes my disk space?" (§5.B.2).</summary>
public sealed record DiskRow(string Title, string Path, long Bytes, List<(string Name, long Bytes)> Children, string? Key = null);

/// <summary>Advice per folder (relative path first, then the last path component), Windows edition of the Mac table.</summary>
public static class DiskAdvice
{
    // Key: path below the user profile (AppData\Local\Temp) or just a folder name (node_modules). Compared case-insensitively.
    static readonly Dictionary<string, (Func<string> Text, Verdict V)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"AppData\Local\Temp"] = (() => L("Temporary files of apps and Windows. Safe to delete; apps recreate what they need"), Verdict.Safe),
        ["Temp"] = (() => L("Temporary files. Safe to delete; they are recreated"), Verdict.Safe),
        ["SoftwareDistribution"] = (() => L("Downloaded Windows updates. Safe to delete once they are installed; Windows downloads them again if needed"), Verdict.Safe),
        ["DeliveryOptimization"] = (() => L("Update files Windows keeps to share with other PCs. Safe to clear in Settings › System › Storage"), Verdict.Safe),
        ["Recycle Bin"] = (() => L("Recycle Bin. Empty it to get the space back"), Verdict.Safe),
        [@"AppData\Local\Packages"] = (() => L("Data of Microsoft Store apps. Keep it; uninstall an app to remove its data"), Verdict.Keep),
        [@"AppData\Local\Programs"] = (() => L("Apps installed for your user only. Remove the ones you don't use in Settings › Apps"), Verdict.Careful),
        [@"AppData\Local\Microsoft"] = (() => L("Data of Microsoft apps, including Edge and OneDrive. Keep it; look inside for what is big"), Verdict.Keep),
        ["CrashDumps"] = (() => L("Crash dump files. Safe to delete"), Verdict.Safe),
        ["npm-cache"] = (() => L("npm download cache. Safe to delete"), Verdict.Safe),
        ["pip"] = (() => L("Python package cache. Safe to delete"), Verdict.Safe),
        ["Docker"] = (() => L("Docker Desktop's disk images. Clean up in Docker itself"), Verdict.Careful),
        ["Google"] = (() => L("Chrome data: history, passwords and cache. Clear the cache in Chrome › Settings › Privacy and security"), Verdict.Careful),
        ["Mozilla"] = (() => L("Firefox data: history, passwords and cache. Clear the cache in Firefox › Settings › Privacy"), Verdict.Careful),
        ["BraveSoftware"] = (() => L("Brave data: history, passwords and cache. Clear the cache in Brave's settings"), Verdict.Careful),
        ["node_modules"] = (() => L("JavaScript packages. Safe to delete; `npm install` brings them back"), Verdict.Safe),
        [".nuget"] = (() => L("NuGet package cache (.NET). Safe to delete; it downloads again"), Verdict.Safe),
        [".npm"] = (() => L("npm download cache. Safe to delete"), Verdict.Safe),
        [".gradle"] = (() => L("Gradle cache (Android/Java builds). Safe to delete; it downloads again"), Verdict.Safe),
        [".android"] = (() => L("Android SDK settings and emulators. Delete old emulators in Android Studio"), Verdict.Careful),
        [".m2"] = (() => L("Maven cache (Java). Safe to delete; it downloads again"), Verdict.Safe),
        [".cargo"] = (() => L("Rust packages and tools. The registry cache can go"), Verdict.Careful),
        [".cache"] = (() => L("Cache of command-line tools. Safe to delete"), Verdict.Safe),
        [".docker"] = (() => L("Docker settings. The big data is in Docker itself: clean up there"), Verdict.Careful),
        [".claude"] = (() => L("Claude Code: your sessions and history. Keep it unless you don't need old conversations"), Verdict.Keep),
        [".vscode"] = (() => L("VS Code extensions. Remove unused extensions in VS Code"), Verdict.Careful),
        ["Downloads"] = (() => L("Your downloads. Often old installers and zips that can go"), Verdict.Careful),
        ["Videos"] = (() => L("Your videos. Keep, or move to an external drive"), Verdict.Keep),
        ["Music"] = (() => L("Your music. Keep, or move to an external drive"), Verdict.Keep),
        ["Pictures"] = (() => L("Your photos. Keep, or move to an external drive"), Verdict.Keep),
        ["Desktop"] = (() => L("Files on your desktop. Your own files"), Verdict.Keep),
        ["Documents"] = (() => L("Your documents. Your own files"), Verdict.Keep),
        ["OneDrive"] = (() => L("Files synced with OneDrive. Use \"Free up space\" in File Explorer to remove local copies"), Verdict.Careful),
        ["Roaming"] = (() => L("Settings and data of your apps. Keep it; look inside for an app you no longer use"), Verdict.Keep),
    };

    /// <summary>Advice for a row: <paramref name="relative"/> = path below the profile (e.g. AppData\Local\Temp), then its last component.</summary>
    public static (string Text, Verdict V)? For(string relative)
    {
        relative = relative.Trim('\\', '/');
        if (Table.TryGetValue(relative, out var a)) return Tr(a);
        int i = relative.LastIndexOfAny(['\\', '/']);
        return Table.TryGetValue(relative[(i + 1)..], out a) ? Tr(a) : null;
    }

    static (string, Verdict) Tr((Func<string> Text, Verdict V) a) => (a.Text(), a.V);

    public static string Label(Verdict v) => v switch { Verdict.Safe => L("can go"), Verdict.Careful => L("check first"), Verdict.Keep => L("keep"), _ => "?" };

    /// <summary>Fallback line for a folder without advice and without big children.</summary>
    public static string Fallback => L("Your own folder. Check what's inside before deleting anything");

    /// <summary>"Biggest inside: name 12.0 GB (can go) · …": up to 3 children over 100 MB, with their verdict label when they have advice.</summary>
    public static string? BiggestInside(DiskRow row, string relative)
    {
        var big = row.Children.Where(c => c.Bytes > 100_000_000).OrderByDescending(c => c.Bytes).Take(3).ToList();
        if (big.Count == 0) return null;
        string Part((string Name, long Bytes) c) =>
            For(relative + "\\" + c.Name) is { } a ? $"{c.Name} {Fmt.Bytes(c.Bytes)} ({Label(a.V)})" : $"{c.Name} {Fmt.Bytes(c.Bytes)}";
        return L("Biggest inside: ") + string.Join(" · ", big.Select(Part));
    }

    /// <summary>Title of a row: "AppData › Local › Temp" for AppData children, "Recycle Bin" etc. for the special ones, else the folder name.</summary>
    public static string Title(string relative) =>
        relative.StartsWith(@"AppData\", StringComparison.OrdinalIgnoreCase) ? string.Join(" › ", relative.Split('\\')) : relative.Replace('\\', ' ').Replace("  ", " ");
}
