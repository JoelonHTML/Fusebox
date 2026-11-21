using Fusebox.Core.Settings;

namespace Fusebox.App;

/// <summary>
/// Settings façade over one <see cref="PrefStore"/> at %LOCALAPPDATA%\Fusebox\settings.json (unpackaged apps have no
/// ApplicationData). Every key and default lives in <see cref="PrefTable"/> (Fusebox.Core/Settings/PrefTable.cs):
/// use the typed reads (<c>Prefs.Bool("onTop")</c>), which return the table default when nothing is stored.
/// A unit test checks that every literal key used with Prefs.* in src/ is in the table.
/// </summary>
public static class Prefs
{
    public static readonly PrefStore Store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fusebox", "settings.json"));

    /// <summary>Raised after a value changed, with its key, on the thread that called Set (marshal to the UI yourself).</summary>
    public static event Action<string>? Changed { add => Store.Changed += value; remove => Store.Changed -= value; }

    public static bool Bool(string key) => Store.Bool(key);
    public static int Int(string key) => Store.Int(key);
    public static long Long(string key) => Store.Long(key);
    public static double Double(string key) => Store.Double(key);
    public static string Str(string key) => Store.String(key);
    public static List<string> List(string key) => Store.List(key);

    /// <summary>Read with an explicit fallback (v1 style). Prefer the typed reads above.</summary>
    public static T Get<T>(string key, T fallback) => Store.Get(key, fallback);

    public static void Set(string key, bool v) => Store.Set(key, v);
    public static void Set(string key, int v) => Store.Set(key, v);
    public static void Set(string key, long v) => Store.Set(key, v);
    public static void Set(string key, double v) => Store.Set(key, v);
    public static void Set(string key, string v) => Store.Set(key, v);
    public static void SetList(string key, IEnumerable<string> items) => Store.SetList(key, items);
    public static void Reset(string key) => Store.Reset(key);
}
