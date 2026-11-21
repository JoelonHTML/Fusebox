using static Fusebox.Core.Strings;

namespace Fusebox.Core.Processes;

/// <summary>One-line plain-language explanations for common processes (English text = translation key, used as "What is this? %@.").</summary>
public static class KnownProcesses
{
    static readonly Dictionary<string, string> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["windows"] = "Drawing the screen, the kernel and system services that Windows itself needs to run",
        ["svchost"] = "A shared host for Windows services",
        ["msmpeng"] = "Microsoft Defender, Windows' built-in virus scanner",
        ["nissrv"] = "Microsoft Defender's network protection",
        ["securityhealthservice"] = "Windows Security, shows the health of your protection",
        ["searchindexer"] = "Windows Search, keeps an index of your files so searching is fast",
        ["searchhost"] = "The Windows search box and Start menu search",
        ["dwm"] = "Desktop Window Manager, draws windows and effects on screen",
        ["explorer"] = "Windows Explorer: the desktop, taskbar, Start menu and file windows",
        ["runtimebroker"] = "Checks the permissions of Microsoft Store apps",
        ["applicationframehost"] = "Shows Microsoft Store apps in a window",
        ["startmenuexperiencehost"] = "The Windows Start menu",
        ["shellexperiencehost"] = "Parts of the Windows taskbar and notifications",
        ["textinputhost"] = "The touch keyboard, emoji panel and typing suggestions",
        ["widgets"] = "The Windows widgets panel",
        ["taskhostw"] = "Runs small Windows background tasks",
        ["ctfmon"] = "Keyboard languages and text input",
        ["audiodg"] = "Windows' audio engine",
        ["spoolsv"] = "The print spooler, sends documents to your printer",
        ["wudfhost"] = "A driver host for devices such as sensors and phones",
        ["conhost"] = "Draws the text window of command-line programs",
        ["windowsterminal"] = "Windows Terminal, the command-line app",
        ["onedrive"] = "OneDrive, syncs your files with the cloud",
        ["msedge"] = "Microsoft Edge, the web browser; every tab and extension is its own process",
        ["msedgewebview2"] = "A web view that other apps use to show web pages",
        ["chrome"] = "Google Chrome, the web browser; every tab and extension is its own process",
        ["firefox"] = "Firefox, the web browser; every tab is its own process",
        ["brave"] = "Brave, the web browser; every tab is its own process",
        ["teams"] = "Microsoft Teams, chat and meetings",
        ["ms-teams"] = "Microsoft Teams, chat and meetings",
        ["code"] = "Visual Studio Code, the code editor",
        ["slack"] = "Slack, team chat",
        ["spotify"] = "Spotify, music player",
        ["discord"] = "Discord, chat and voice",
        ["steam"] = "Steam, the game store and launcher",
        ["searchprotocolhost"] = "Windows Search, keeps an index of your files so searching is fast",
        ["searchfilterhost"] = "Windows Search, keeps an index of your files so searching is fast",
        ["tiworker"] = "Checks for or installs Windows updates",
        ["trustedinstaller"] = "Checks for or installs Windows updates",
        ["taskmgr"] = "Task Manager",
        ["fusebox"] = "Fusebox, this app",
    };

    public const string WindowsName = "Windows itself";

    /// <summary>Plain-language names (English; show with <see cref="Display"/>) for processes whose file description is cryptic.</summary>
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["searchindexer"] = "Windows Search", ["searchprotocolhost"] = "Windows Search", ["searchfilterhost"] = "Windows Search",
        ["msmpeng"] = "Virus scanner (Microsoft Defender)", ["nissrv"] = "Virus scanner (Microsoft Defender)",
        ["tiworker"] = "Windows Update", ["trustedinstaller"] = "Windows Update",
        ["audiodg"] = "Sound", ["explorer"] = "File Explorer and taskbar",
    };

    /// <summary>English explanation (the data); <see cref="Display"/> translates it.</summary>
    public static string? Explain(string imageName) => Table.GetValueOrDefault(imageName);
    public static string? FriendlyName(string imageName) => Names.GetValueOrDefault(imageName);

    /// <summary>Translates a name or explanation from the tables above (unknown text comes back unchanged). The strings-check collects this file's literals.</summary>
    public static string Display(string english) => english.Length == 0 ? english : L(english);

    /// <summary>True when <paramref name="name"/> is one of our own names (so it is translated, a file description never is).</summary>
    public static bool IsOwnName(string name) => name == WindowsName || Names.ContainsValue(name);
}
