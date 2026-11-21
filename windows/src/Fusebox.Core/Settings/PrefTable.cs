namespace Fusebox.Core.Settings;

/// <summary>
/// Shared = exported and imported (settings file for friends). Local = belongs to this PC: never exported or imported,
/// but reset by "Reset everything". Bookkeeping = app state (window sizes, updater): never exported, imported or reset.
/// </summary>
public enum PrefScope { Shared, Local, Bookkeeping }

/// <summary>One setting. Default is bool, long (all integer prefs), double or string (lists are comma strings).</summary>
public sealed record Pref(string Key, object Default, PrefScope Scope = PrefScope.Shared);

/// <summary>
/// Every preference of the app in one table: the Mac's keys and defaults (FEATURES.md §9.17, Sources/Prefs.swift) plus the
/// Windows additions. Stored values are short fixed ids, never display text. New setting = one line here.
/// Windows differences: hotkey keys hold Windows values (VK code + MOD_* flags) and are Local (a Mac file has Carbon codes);
/// lastSeenBuild is Bookkeeping; dockedDisplays/dockedWireless/appLanguage are in the table (spec §12.1 #3).
/// </summary>
public static class PrefTable
{
    const PrefScope L = PrefScope.Local, B = PrefScope.Bookkeeping;

    /// <summary>Alert kinds with their Mac start values (Model.swift Alert.start): on, limit, minutes in a row.</summary>
    public static readonly (string Id, bool On, double Limit, long Minutes)[] Alerts =
    [
        ("cpu", true, 90, 5), ("heat", true, 100, 5), ("mem", true, 4, 2), ("disk", true, 10, 1),
        ("net", false, 300, 3), ("battery", false, 20, 1), ("app", false, 50, 10),
    ];

    /// <summary>Default fan curve "°C:%" pairs (Mac FanCurve.balanced). Fans are hidden on Windows; kept for compatibility.</summary>
    public const string BalancedFanCurve = "40:0,60:20,75:50,85:80,95:100";

    public static readonly IReadOnlyList<Pref> All = Build();

    public static readonly IReadOnlyDictionary<string, Pref> ByKey = All.ToDictionary(p => p.Key);

    public static bool Known(string key) => ByKey.ContainsKey(key);

    static List<Pref> Build()
    {
        var l = new List<Pref>
        {
            // Look (Settings › Appearance)
            new("theme", "instrument"),            // instrument | terminal | minimal | glas | blokken | liquid
            new("appearanceMode", "system"),       // system | light | dark
            new("accent", ""),                     // "#RRGGBB", "" = theme accent
            new("textScale", 1.0),                 // 1.0 / 1.15 / 1.3, clamped 0.8–1.6 when read
            new("font", "style"),                  // style | sfPro | rounded | mono | serif | montserrat | ubuntu
            new("numberStyle", "fade"),            // roll | fade | instant
            new("lowImpact", "auto"),              // off | on | auto (the choice; LowImpact.On is the live state)
            new("page", "live", L),                // current window tab (app always opens on Live)
            new("settingsTab", "profile", L),
            new("dockIcon", true),                 // Windows: taskbar button
            new("keepRunning", true),
            new("autoUpdate", true, L),
            new("onTop", false),
            new("hotkeyCode", 0x46L, L),           // Windows virtual key ('F'); −1 = off
            new("hotkeyMods", 3L, L),              // MOD_ALT 1 | MOD_CONTROL 2 (Win 8, Shift 4)
            new("hotkeyLabel", "Ctrl+Alt+F", L),
            new("appLanguage", "", L),             // "" = system, en | nl | de | fr | es
            // Live page
            new("metrics", "cpu,gpu,ram,net,ssd,battery"),   // cpu,gpu,ram,net,ssd,io,battery (fan: Mac only)
            new("metricsHidden", ""),
            new("graphs", "auto"),                 // auto | always | never
            new("shortMinutes", 5L),               // 1,5,10,15,30,60
            new("absolute", "ram,ssd"),            // subset of cpu,ram,ssd
            new("showAdvice", false),
            new("netBits", false),
            new("perCore", false),
            new("batteryTime", true),
            new("learnedBattery", true),
            new("netSpeed", false),
            new("netPing", false),
            new("cpuGraph", "usage"),              // usage | temp | both
            new("gpuGraph", "usage"),
            new("warnCPU", 85.0),
            new("warnTemp", 95.0),
            new("warnPing", 100.0),
            new("smoothBig", true),
            // Processes
            new("procFilter", "all"),              // all | apps | background | macos (Windows: the "Windows" chip keeps id macos)
            new("procSort", "cpu"),                // cpu | ram | energy | gpu | name
            new("procHide", ""),                   // hidden columns: disk,net,energy,gpu,threads,running
            new("procSmall", "idle"),              // all | idle | light
            new("netPerApp", false),
            // Checkup
            new("checkupStart", "last"),           // last | today | yesterday | week
            new("checkupHide", ""),
            new("restartDays", 14L),               // 7 / 14 / 30 / 0
            // Network
            new("netAutoScan", true),
            new("notifyNewDevice", false),
            new("netHide", ""),                    // hidden columns: mac,vendor,kind ("" = MAC visible, PLAN §7.8)
            new("speedMB", false),
            // Docked
            new("dockedInternals", false),
            new("dockedEmpty", true),
            new("dockedBytes", false),
            new("dockedDisplays", true),
            new("dockedWireless", true),
            // Menu bar → tray icon
            new("menubarMetrics", "cpu"),          // max 4
            new("menubarMetricsHidden", ""),
            new("menubarStyle", "text"),           // text | compact | bars | graph | icon | off
            new("menubarSplit", false),
            new("menuStyle", "panel"),             // menu | panel
            new("menuParts", "status,values,top,links"),
            new("richPanel", true),
            // Notifications
            new("notify", true),
            new("alertCooldown", 30L),             // 5–240
            new("alert.peak.on", true),
            new("alert.peak.min", 2L),
            // Data and privacy
            new("keepDays", 30L, L),               // 7 / 30 / 90 / 365 / 0 = forever
            new("pingHost", "1.1.1.1", L),         // "" = off
            // Fans and charge limit: Mac only (hidden on Windows), kept so files round-trip
            new("fanMode", "auto", L),
            new("fanCurve", BalancedFanCurve, L),
            new("chargeLimitOn", false, L),
            new("chargeLimit", 80L, L),
            new("chargeHeat", false, L),
            new("setupDone", false, L),
            // Bookkeeping (never exported, imported or reset)
            new("lastSeenBuild", 0L, B),
            new("liveSize", "", B),                // "w,h" in DIPs; "" = 300,340
            new("windowSize", "", B),              // "w,h" in DIPs, all tabs except Live
            new("windowTopLeft", "", B),           // "x,y" in physical screen pixels
            new("failedUpdateBuild", 0L, B),
            new("notifiedUpdate", "", B),
            new("notifyAskRound", 0L, B),
            new("lanAsked", false, B),
            new("ssdBenchLast", "", B),
            new("speedHistory", "", B),
            new("netFindNow", false, B),
            new("netRecentRanges", "", B),
            new("netPanelOpen", false, B),
            new("checkupOpen", "", B),
            new("analysisPeriod", "", B),
            new("appPeriod", "", B),
            new("installAsked", false, B),         // Windows: "Move to Programs" asked once
        };
        foreach (var a in Alerts)
        {
            l.Add(new($"alert.{a.Id}.on", a.On));
            l.Add(new($"alert.{a.Id}.limit", a.Limit));
            l.Add(new($"alert.{a.Id}.min", a.Minutes));
        }
        return l;
    }
}

/// <summary>The four profiles of Settings › Profile (Mac Prefs.presets). Titles/blurbs are English keys for Strings.L.</summary>
public sealed record Profile(string Id, string Title, string Blurb, IReadOnlyDictionary<string, object> Values)
{
    public static readonly IReadOnlyList<Profile> All =
    [
        new("simple", "Simple", "Big, calm, with advice", new Dictionary<string, object>
        {
            ["theme"] = "liquid", ["textScale"] = 1.15, ["metrics"] = "cpu,ram,ssd,battery", ["graphs"] = "never",
            ["showAdvice"] = true, ["menubarStyle"] = "icon",
        }),
        new("gamer", "Gamer", "GPU and ping up front", new Dictionary<string, object>
        {
            ["theme"] = "instrument", ["metrics"] = "cpu,gpu,ram,net", ["graphs"] = "auto", ["shortMinutes"] = 5L,
            ["menubarStyle"] = "compact", ["menubarMetrics"] = "cpu,gpu", ["netBits"] = true, ["warnPing"] = 60.0,
        }),
        new("nerd", "Nerd", "Everything, in real values", new Dictionary<string, object>
        {
            ["theme"] = "terminal", ["metrics"] = "cpu,gpu,ram,net,ssd,io,battery", ["absolute"] = "cpu,ram,ssd",
            ["menubarStyle"] = "graph", ["menubarMetrics"] = "cpu,ram,net",
        }),
        new("laptop", "Laptop", "Battery first, alert when almost empty", new Dictionary<string, object>
        {
            ["theme"] = "minimal", ["metrics"] = "battery,cpu,ram,ssd", ["menubarStyle"] = "text",
            ["menubarMetrics"] = "battery,cpu", ["alert.battery.on"] = true,
        }),
    ];

    /// <summary>Every key any profile touches: reset to default before a profile is applied.</summary>
    public static readonly IReadOnlySet<string> TouchedKeys = All.SelectMany(p => p.Values.Keys).ToHashSet();
}
