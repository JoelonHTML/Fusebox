using Fusebox.Core.Alerts;
using Fusebox.Core.Look;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Settings;

public enum RecordResult { Ignore, Cancel, Off, Reject, Accept }

/// <summary>
/// Hotkey recorder rules (§11.3): Esc cancels, Delete/Backspace turns the shortcut off, a key needs Ctrl or Alt (Shift optional),
/// the label is "Ctrl+Alt+Shift+Win+F". Codes are Windows virtual keys, mods the MOD_* flags of RegisterHotKey.
/// </summary>
public static class HotkeyLogic
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    /// <summary>Display name of a virtual key, null when it can't be a shortcut key.</summary>
    public static string? KeyName(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return vk switch
        {
            0x20 => "Space", 0x09 => "Tab", 0x0D => "Enter", 0x21 => "PgUp", 0x22 => "PgDn", 0x23 => "End", 0x24 => "Home",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x2D => "Insert",
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`", 0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
            _ => null,
        };
    }

    public static string Label(int vk, int mods)
    {
        var p = new List<string>();
        if ((mods & Ctrl) != 0) p.Add("Ctrl");
        if ((mods & Alt) != 0) p.Add("Alt");
        if ((mods & Shift) != 0) p.Add("Shift");
        if ((mods & Win) != 0) p.Add("Win");
        p.Add(KeyName(vk) ?? "?");
        return string.Join('+', p);
    }

    /// <summary>What a key press means while recording. Modifier keys alone are ignored (keep waiting).</summary>
    public static (RecordResult Result, int Code, int Mods, string Label) Record(int vk, bool ctrl, bool alt, bool shift)
    {
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) return (RecordResult.Ignore, 0, 0, "");
        if (vk == 0x1B) return (RecordResult.Cancel, 0, 0, "");
        if (vk is 0x2E or 0x08) return (RecordResult.Off, -1, 0, "");
        if (!(ctrl || alt) || KeyName(vk) == null) return (RecordResult.Reject, 0, 0, "");
        int mods = (alt ? Alt : 0) | (ctrl ? Ctrl : 0) | (shift ? Shift : 0);
        return (RecordResult.Accept, vk, mods, Label(vk, mods));
    }
}

/// <summary>Texts the Settings pages and the setup build from stored values.</summary>
public static class SettingsText
{
    /// <summary>The Mac's "changes" line of a profile: "Liquid Glass · large text · CPU RAM SSD BAT · no graphs · advice line · tray icon".</summary>
    public static string Changes(Profile p)
    {
        var v = p.Values;
        var o = new List<string>();
        if (v.TryGetValue("theme", out var t)) o.Add(Themes.Named((string)t).Name);
        if (v.TryGetValue("textScale", out var x) && x is double sc && sc > 1) o.Add(sc >= 1.3 ? L("extra large text") : L("large text"));
        if (v.TryGetValue("metrics", out var m)) o.Add(string.Join(' ', Metrics.Parse((string)m).Select(Metrics.Label)));
        if (v.TryGetValue("graphs", out var g) && (string)g == "never") o.Add(L("no graphs"));
        if (v.TryGetValue("showAdvice", out var a) && a is true) o.Add(L("advice line"));
        if (v.ContainsKey("absolute")) o.Add(L("real values"));
        if (v.TryGetValue("netBits", out var nb) && nb is true) o.Add("Mbit/s");
        if (v.TryGetValue("menubarStyle", out var ms))
            o.Add((string)ms == "icon" ? L("tray icon") : L("tray %@", TrayStyleWord((string)ms)));
        if (v.TryGetValue("alert.battery.on", out var b) && b is true) o.Add(L("battery alert"));
        return string.Join(" · ", o);
    }

    /// <summary>First word of the style title, lowercased ("Text (CPU 23%)" → "text"); stays English like the Mac's argument.</summary>
    public static string TrayStyleWord(string style) => style switch { "compact" => "compact", "bars" => "bars", "graph" => "graphs", _ => "text" };

    /// <summary>Normal / large / extra large / custom text, for the setup summary.</summary>
    public static string TextSize(double scale) => Math.Abs(scale - 1) < 0.01 ? L("normal") : Math.Abs(scale - 1.15) < 0.01 ? L("large")
        : Math.Abs(scale - 1.3) < 0.01 ? L("extra large") : L("custom");

    /// <summary>Metric labels joined ", " (setup summary: "CPU, GPU, RAM").</summary>
    public static string MetricList(string metrics) => string.Join(", ", Metrics.Parse(metrics).Select(Metrics.Label));

    /// <summary>Number of alert kinds switched on (setup summary "n kinds on").</summary>
    public static int KindsOn(Func<string, bool> on) => AlertKinds.Ids.Count(id => on(AlertKinds.Key(id, "on")));

    /// <summary>Home folder replaced by "~" (Settings folder display, performance report).</summary>
    public static string Tilde(string path, string home)
    {
        if (home.Length == 0) return path;
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) && (path.Length == home.Length || path[home.Length] is '\\' or '/')
            ? "~" + path[home.Length..] : path;
    }
}
