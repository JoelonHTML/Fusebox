using System.Text.Json.Nodes;

namespace Fusebox.App;

/// <summary>Settings in %LOCALAPPDATA%\Fusebox\settings.json (unpackaged apps have no ApplicationData). JsonNode = AOT-safe.</summary>
public static class Prefs
{
    static readonly string Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fusebox", "settings.json");
    static JsonObject? data;

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
        try { return Data[key] is JsonValue v && v.TryGetValue<T>(out var t) ? t : fallback; } catch { return fallback; }
    }

    public static void Set<T>(string key, T value)
    {
        Data[key] = JsonValue.Create(value);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, Data.ToJsonString());
        File.Move(tmp, Path, overwrite: true);
    }
}
