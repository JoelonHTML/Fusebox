using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fusebox.Core.Settings;

/// <summary>
/// The settings file (JSON object, key → bool/number/string) with typed reads that fall back to <see cref="PrefTable"/>,
/// v1 migration, export/import (trust boundary) and profiles. Thread-safe; JsonNode only (AOT-safe).
/// App code uses the static <c>Fusebox.App.Prefs</c> façade over one instance at %LOCALAPPDATA%\Fusebox\settings.json.
/// </summary>
public sealed class PrefStore
{
    public const int MaxImportBytes = 100_000, MaxString = 200;
    readonly string? path;
    readonly object gate = new();
    readonly JsonObject data;

    /// <summary>Raised after a value really changed (also for each key of a bulk change), on the thread that changed it.</summary>
    public event Action<string>? Changed;

    /// <param name="path">Settings file; null = memory only (tests).</param>
    public PrefStore(string? path)
    {
        this.path = path;
        JsonObject? d = null;
        if (path != null) try { d = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; } catch { }
        data = d ?? new JsonObject();
        if (Migrate(data)) Save();
    }

    // ---- reads: stored value of the right type, else the table default ----

    public bool Bool(string key) => Read(key, out var n) && n.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? n.GetValue<bool>() : Def<bool>(key);
    public long Long(string key) => Read(key, out var n) && Num(n, out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (long)d : Def<long>(key);
    public int Int(string key) => (int)Math.Clamp(Long(key), int.MinValue, int.MaxValue);
    public double Double(string key) => Read(key, out var n) && Num(n, out var d) && double.IsFinite(d) ? d : Def<double>(key);
    public string String(string key) => Read(key, out var n) && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : Def<string>(key) ?? "";

    /// <summary>Comma list (metrics, absolute, procHide …): trimmed, empty items dropped, order kept.</summary>
    public List<string> List(string key) => [.. String(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>v1-style read with an explicit fallback (bool, int, long, double, string). Prefer the typed reads.</summary>
    public T Get<T>(string key, T fallback)
    {
        if (!Read(key, out var n)) return fallback;
        object? v = fallback switch
        {
            bool when n.GetValueKind() is JsonValueKind.True or JsonValueKind.False => n.GetValue<bool>(),
            string when n.GetValueKind() == JsonValueKind.String => n.GetValue<string>(),
            int when Num(n, out var d) && d == Math.Floor(d) => (int)Math.Clamp(d, int.MinValue, int.MaxValue),
            long when Num(n, out var d) && d == Math.Floor(d) => (long)d,
            double when Num(n, out var d) && double.IsFinite(d) => d,
            _ => null,
        };
        return v is T t ? t : fallback;
    }

    public bool IsSet(string key) { lock (gate) return data.ContainsKey(key); }

    // ---- writes ----

    public void Set(string key, bool v) => Write(key, JsonValue.Create(v));
    public void Set(string key, long v) => Write(key, JsonValue.Create(v));
    public void Set(string key, int v) => Write(key, JsonValue.Create((long)v));
    public void Set(string key, double v) { if (double.IsFinite(v)) Write(key, JsonValue.Create(v)); }
    public void Set(string key, string v) => Write(key, JsonValue.Create(v));
    public void SetList(string key, IEnumerable<string> items) => Set(key, string.Join(',', items));

    /// <summary>Object overload for table-driven code (profiles, tests): bool, int, long, double or string.</summary>
    public void SetObject(string key, object v)
    {
        switch (v)
        {
            case bool b: Set(key, b); break;
            case int i: Set(key, i); break;
            case long l: Set(key, l); break;
            case double d: Set(key, d); break;
            case string s: Set(key, s); break;
            default: throw new NotSupportedException(v.GetType().Name);
        }
    }

    /// <summary>Back to the default (removes the stored value).</summary>
    public void Reset(string key) => Write(key, null, remove: true);

    // ---- bulk: profiles, reset, export, import ----

    /// <summary>Profile: first every key any profile touches back to default, then this profile's values. Logs and ping stay.</summary>
    public void ApplyProfile(string id)
    {
        var p = Profile.All.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException(id);
        foreach (var k in Profile.TouchedKeys) Reset(k);
        foreach (var (k, v) in p.Values) SetObject(k, v);
        EnforceReachable();
    }

    /// <summary>"Reset everything": every Shared and Local setting back to default except setupDone. Bookkeeping stays.</summary>
    public void ResetAll()
    {
        foreach (var p in PrefTable.All)
            if (p.Scope != PrefScope.Bookkeeping && p.Key != "setupDone") Reset(p.Key);
        EnforceReachable();
    }

    /// <summary>Fusebox-settings.json: pretty, sorted, every Shared key with its current value (Local/Bookkeeping never leave this PC).</summary>
    public string Export()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var p in PrefTable.All.Where(p => p.Scope == PrefScope.Shared).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                w.WritePropertyName(p.Key);
                switch (p.Default)
                {
                    case bool: w.WriteBooleanValue(Bool(p.Key)); break;
                    case long: w.WriteNumberValue(Long(p.Key)); break;
                    case double: w.WriteNumberValue(Double(p.Key)); break;
                    default: w.WriteStringValue(String(p.Key)); break;
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Settings from a friend's file. Null = not readable (too big, not a JSON object); else how many were applied.</summary>
    public int? ImportFile(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length >= MaxImportBytes) return null;
            return Import(File.ReadAllText(file));
        }
        catch { return null; }
    }

    /// <summary>
    /// Trust boundary (Mac Prefs.apply): only known Shared keys, only values of the default's type — bool must be a real JSON bool,
    /// an integer pref an exact integer, a double finite, a string ≤ 200 chars. Null = not a JSON object.
    /// </summary>
    public int? Import(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) >= MaxImportBytes) return null;
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; } catch { return null; }
        if (o == null) return null;
        int n = 0;
        foreach (var (key, raw) in o.ToList())
        {
            if (!PrefTable.ByKey.TryGetValue(key, out var p) || p.Scope != PrefScope.Shared || raw is not JsonValue v) continue;
            var kind = v.GetValueKind();
            switch (p.Default)
            {
                case bool when kind is JsonValueKind.True or JsonValueKind.False: Set(key, v.GetValue<bool>()); n++; break;
                case long when Num(v, out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15: Set(key, (long)d); n++; break;
                case double when Num(v, out var d) && double.IsFinite(d): Set(key, d); n++; break;
                case string when kind == JsonValueKind.String && v.GetValue<string>() is { Length: <= MaxString } s: Set(key, s); n++; break;
            }
        }
        EnforceReachable();
        return n;
    }

    /// <summary>Never hide both the taskbar button (dockIcon) and the tray icon (menubarStyle off): Fusebox would be unreachable.</summary>
    public void EnforceReachable()
    {
        if (!Bool("dockIcon") && String("menubarStyle") == "off") Set("dockIcon", true);
    }

    // ---- v1 → v2 migration (pure; idempotent; only touches keys that are present) ----

    /// <summary>Renames v1 keys and ids to the Mac's. Returns true when something changed.</summary>
    public static bool Migrate(JsonObject d)
    {
        bool changed = false;
        string? Str(string k) => d[k] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        bool? Flag(string k) => d[k] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;
        void Put(string k, JsonNode? v) { d[k] = v; changed = true; }
        void Drop(string k) { if (d.Remove(k)) changed = true; }
        void Move(string from, string to, Func<JsonNode, JsonNode?>? map = null)
        {
            if (!d.TryGetPropertyValue(from, out var v)) return;
            d.Remove(from);
            if (v != null && !d.ContainsKey(to)) d[to] = map != null ? map(v.DeepClone()) : v.DeepClone();
            changed = true;
        }

        Move("language", "appLanguage", v => v is JsonValue s && s.GetValueKind() == JsonValueKind.String && s.GetValue<string>() == "system" ? JsonValue.Create("") : v);
        Move("appearance", "appearanceMode");
        Move("logDays", "keepDays");
        if (Str("theme") is { } t && t is "glass" or "blocks") Put("theme", t == "glass" ? "glas" : "blokken");
        if (Str("font") is { } f && f is "segoe" or "cascadia") Put("font", f == "segoe" ? "sfPro" : "mono");
        // v1: "lowImpactMode" = the choice, "lowImpact" = the live state (bool). Mac: "lowImpact" = the choice.
        if (Flag("lowImpact") != null) Drop("lowImpact");
        Move("lowImpactMode", "lowImpact");
        foreach (var k in new[] { "tray", "trayIcon" })
        {
            if (Flag(k) == false && !d.ContainsKey("menubarStyle")) Put("menubarStyle", "off");
            Drop(k);
        }
        if (Flag("hotkey") is { } hk) { if (!hk) Put("hotkeyCode", -1L); Drop("hotkey"); }
        if (Str("windowRect") is { } rect)
        {
            var p = rect.Split(',');
            if (p.Length == 4 && !d.ContainsKey("windowTopLeft")) Put("windowTopLeft", $"{p[0]},{p[1]}");
            Drop("windowRect");
        }
        foreach (var k in new[] { "metrics", "metricsHidden" })
            if (Str(k) is { } list && list.Split(',').Contains("bat"))
                Put(k, string.Join(',', list.Split(',').Select(x => x == "bat" ? "battery" : x)));
        return changed;
    }

    // ---- internals ----

    bool Read(string key, out JsonValue n)
    {
        lock (gate)
        {
            if (data[key] is JsonValue v) { n = (JsonValue)v.DeepClone(); return true; }
        }
        n = null!;
        return false;
    }

    static T Def<T>(string key) => PrefTable.ByKey.TryGetValue(key, out var p) && p.Default is T t ? t : default!;

    static bool Num(JsonValue v, out double d)
    {
        d = 0;
        return v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
    }

    void Write(string key, JsonNode? node, bool remove = false)
    {
        lock (gate)
        {
            var old = data[key];
            if (remove ? old == null && !data.ContainsKey(key) : old != null && JsonNode.DeepEquals(old, node)) return;
            if (remove) data.Remove(key); else data[key] = node;
            Save();
        }
        Changed?.Invoke(key);
    }

    void Save()
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, data.ToJsonString());
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* disk full / read-only: keep the in-memory value */ }
    }
}
