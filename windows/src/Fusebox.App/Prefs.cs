using System.Text.Json.Nodes;

namespace Fusebox.App;

/// <summary>Settings in %LOCALAPPDATA%\Fusebox\settings.json (unpackaged apps have no ApplicationData). JsonNode = AOT-safe.</summary>
public static class Prefs
{
    static readonly string Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fusebox", "settings.json");
    static readonly object Gate = new();
    static JsonObject? data;

    /// <summary>Raised after a value was written, with its key. May run on any thread that called Set.</summary>
    public static event Action<string>? Changed;

    static JsonObject Data
    {
        get
        {
            if (data != null) return data;
            try { data = JsonNode.Parse(File.ReadAllText(Path)) as JsonObject; } catch { }
            return data ??= new JsonObject();
        }
    }

    public static T Get<T>(string key, T fallback)
    {
        try { lock (Gate) return Data[key] is JsonValue v && v.TryGetValue<T>(out var t) ? t : fallback; } catch { return fallback; }
    }

    public static void Set<T>(string key, T value)
    {
        // Explicit node types: JsonValue.Create<T> would need reflection (not AOT-safe).
        JsonNode? node = value switch
        {
            bool b => JsonValue.Create(b),
            string s => JsonValue.Create(s),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            double d => JsonValue.Create(d),
            _ => throw new NotSupportedException(typeof(T).Name),
        };
        lock (Gate)
        {
            Data[key] = node;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, Data.ToJsonString());
                File.Move(tmp, Path, overwrite: true);
            }
            catch { /* disk full / read-only: keep the in-memory value */ }
        }
        Changed?.Invoke(key);
    }
}
