using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Fusebox.Core;

/// <summary>
/// Same model as the Mac app: the English text is the key. Translations come from the Mac's
/// Resources/*.lproj/Localizable.strings (read-only) plus windows/Strings/&lt;lang&gt;.strings, which wins.
/// Placeholders use the Mac's printf style (%@, %lld, %d, %.1f, %1$@ …) so keys match the Mac files.
/// </summary>
public static partial class Strings
{
    public static readonly string[] Languages = ["en", "nl", "de", "fr", "es"];
    static Dictionary<string, string> table = new();
    public static string Language { get; private set; } = "en";

    /// <summary>"system" or empty = Windows display language, falls back to English.</summary>
    public static void Load(string? language)
    {
        var lang = string.IsNullOrEmpty(language) || language == "system"
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : language;
        if (!Languages.Contains(lang)) lang = "en";
        Language = lang;
        var t = new Dictionary<string, string>();
        if (lang != "en") Merge(t, Read($"mac.{lang}.strings"));
        MergeWindows(t, lang);
        table = t;
    }

    static void Merge(Dictionary<string, string> into, Dictionary<string, string> from)
    {
        foreach (var (k, v) in from) into[k] = v;
    }

    /// <summary>windows/Strings/&lt;lang&gt;.strings plus per-area files windows/Strings/&lt;area&gt;.&lt;lang&gt;.strings (all win over the Mac).</summary>
    static void MergeWindows(Dictionary<string, string> t, string lang)
    {
        Merge(t, Read($"win.{lang}.strings"));
        foreach (var n in typeof(Strings).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("win.") && n.EndsWith($".{lang}.strings") && n != $"win.{lang}.strings").Order())
            Merge(t, Read(n));
    }

    static Dictionary<string, string> Read(string resource)
    {
        using var s = typeof(Strings).Assembly.GetManifestResourceStream(resource);
        if (s == null) return new();
        using var r = new StreamReader(s, Encoding.UTF8);
        return Parse(r.ReadToEnd());
    }

    /// <summary>Parses Apple .strings: "key" = "value"; with /* */ and // comments and \" \n \\ \t escapes.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>();
        int i = 0;
        string? key = null;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { i = text.IndexOf("*/", i + 2) is var e and >= 0 ? e + 2 : text.Length; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { i = text.IndexOf('\n', i) is var e and >= 0 ? e : text.Length; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => text[i] });
                    }
                    else sb.Append(text[i]);
                    i++;
                }
                i++;
                if (key == null) key = sb.ToString();
                else { d[key] = sb.ToString(); key = null; }
                continue;
            }
            if (c == ';') key = null;
            i++;
        }
        return d;
    }

    /// <summary>Translate. Args fill %@ / %lld / %d / %.Nf / %1$@ placeholders in order (or by position).</summary>
    public static string L(string key, params object[] args)
    {
        var s = table.TryGetValue(key, out var v) && v.Length > 0 ? v : key;
        return args.Length == 0 ? s : Format(s, args);
    }

    [GeneratedRegex(@"%(?:(\d+)\$)?([-+ 0#]*\d*)(?:\.(\d+))?(ll|l|h)?([@dDiuf%s])")]
    private static partial Regex Spec();

    public static string Format(string s, object[] args)
    {
        int next = 0;
        return Spec().Replace(s, m =>
        {
            if (m.Groups[5].Value == "%") return "%";
            int idx = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) - 1 : next++;
            if (idx < 0 || idx >= args.Length) return m.Value;
            var a = args[idx];
            if (m.Groups[5].Value == "f" && a is IConvertible)
            {
                int digits = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 6;
                return Convert.ToDouble(a, CultureInfo.InvariantCulture).ToString("F" + digits, Culture);
            }
            return Convert.ToString(a, Culture) ?? "";
        });
    }

    public static CultureInfo Culture => Language == "en" ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo(Language);

    /// <summary>All keys known for a language (for the translation check in tests).</summary>
    public static IReadOnlyDictionary<string, string> TableFor(string lang)
    {
        var t = new Dictionary<string, string>();
        Merge(t, Read($"mac.{lang}.strings"));
        MergeWindows(t, lang);
        return t;
    }
}
