using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fusebox.App;

/// <summary>
/// Styles (Glass, Minimal light, Color blocks, Instrument, Terminal), Appearance (System/Light/Dark) and Font.
/// Windows call <see cref="Apply"/> once (MainWindow ctor, after InitializeComponent; also the tray panel); after that they follow
/// Prefs "theme", "appearance", "font" and low impact by themselves. Style = brush colors in Themes/Fusebox.xaml mutated in place
/// (live for every ThemeResource), corner radius for pages opened afterwards, font + page color on the window's root content.
/// </summary>
public static class Theming
{
    public sealed record Style(string Id, string Name, double Radius, string Font,
        string CardDark, string BorderDark, string GraphDark, string? PageDark,
        string CardLight, string BorderLight, string GraphLight, string? PageLight);

    // Names are never translated. Segoe UI Variable = the Windows system font; Cascadia Mono = its mono font.
    public static readonly Style[] Styles =
    [
        new("glass", "Glass", 12, "segoe", "#14FFFFFF", "#1FFFFFFF", "#4CC2FF", null, "#B3FFFFFF", "#14000000", "#005FB8", null),
        new("minimal", "Minimal light", 4, "segoe", "#262626", "#3A3A3A", "#4DA3FF", "#1C1C1C", "#F7F7F7", "#E6E6E6", "#006BD9", "#FFFFFF"),
        new("blocks", "Color blocks", 14, "montserrat", "#2C2A27", "#403D38", "#5BA3F0", "#1F1E1C", "#FFFFFF", "#D9D4C5", "#2E7DD6", "#F5F2EB"),
        new("instrument", "Instrument", 6, "segoe", "#263347", "#2B3B4F", "#F5B83D", "#1F2B3D", "#FFFFFF", "#D8DEE8", "#B8820A", "#EEF1F6"),
        new("terminal", "Terminal", 0, "cascadia", "#121A14", "#1C2921", "#7DFC99", "#0A0F0D", "#F2F8F3", "#CFE5D4", "#1E8F3E", "#F4FAF5"),
    ];

    /// <summary>(id, label) pairs for the Font picker. "style" = whatever belongs to the style.</summary>
    public static readonly (string Id, string Family)[] Fonts =
    [
        ("segoe", "Segoe UI Variable Text"),
        ("cascadia", "Cascadia Mono, Consolas"),
        // Bundled TTFs are copied to Assets\Fonts next to the exe (csproj); fallback = Segoe in case the URI does not resolve.
        ("montserrat", "ms-appx:///Assets/Fonts/Montserrat%5Bwght%5D.ttf#Montserrat, Segoe UI Variable Text"),
        ("ubuntu", "ms-appx:///Assets/Fonts/Ubuntu-Regular.ttf#Ubuntu, Segoe UI Variable Text"),
    ];

    static readonly List<WeakReference<Window>> windows = [];
    static bool hooked;

    public static Style Current => Styles.FirstOrDefault(s => s.Id == Prefs.Get("theme", "glass")) ?? Styles[0];

    public static void Apply(Window w)
    {
        windows.Add(new(w));
        if (!hooked)
        {
            hooked = true;
            Prefs.Changed += k => { if (k is "theme" or "appearance" or "font" or "lowImpact") Refresh(); };
        }
        Update(w);
    }

    static void Refresh()
    {
        foreach (var r in windows.ToArray())
        {
            if (!r.TryGetTarget(out var w)) { windows.Remove(r); continue; }
            if (!w.DispatcherQueue.TryEnqueue(() => Update(w))) windows.Remove(r);
        }
    }

    static void Update(Window w)
    {
        var s = Current;
        try { UpdateResources(s); } catch { }
        // Glass = acrylic blur (heavier); low impact or any other style = Mica.
        try
        {
            bool glass = s.Id == "glass" && !LowImpact.On;
            if (glass != (w.SystemBackdrop is DesktopAcrylicBackdrop)) w.SystemBackdrop = glass ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
        }
        catch { }
        if (w.Content is not FrameworkElement root) return;
        root.RequestedTheme = Prefs.Get("appearance", "system") switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        if (root is Control c)
        {
            c.FontFamily = new FontFamily(FamilyFor(s));
            bool dark = root.ActualTheme == ElementTheme.Dark;
            var page = dark ? s.PageDark : s.PageLight;
            c.Background = page == null ? null : new SolidColorBrush(Hex(page));
        }
    }

    static string FamilyFor(Style s)
    {
        var id = Prefs.Get("font", "style");
        if (id == "style") id = s.Font;
        return Fonts.FirstOrDefault(f => f.Id == id).Family ?? Fonts[0].Family;
    }

    static void UpdateResources(Style s)
    {
        foreach (var m in Application.Current.Resources.MergedDictionaries)
        {
            if (!m.ContainsKey("FuseboxCardCornerRadius")) continue;
            m["FuseboxCardCornerRadius"] = new CornerRadius(s.Radius);
            m["FuseboxFont"] = new FontFamily(FamilyFor(s));
            foreach (var (key, light) in new[] { ("Default", false), ("Light", true) })
            {
                if (!m.ThemeDictionaries.TryGetValue(key, out var o) || o is not ResourceDictionary d) continue;
                Set(d, "FuseboxCardBackground", light ? s.CardLight : s.CardDark);
                Set(d, "FuseboxCardBorder", light ? s.BorderLight : s.BorderDark);
                Set(d, "FuseboxGraphStroke", light ? s.GraphLight : s.GraphDark);
            }
        }
    }

    static void Set(ResourceDictionary d, string key, string hex)
    {
        if (d[key] is SolidColorBrush b) b.Color = Hex(hex);
    }

    /// <summary>#RRGGBB or #AARRGGBB</summary>
    static Windows.UI.Color Hex(string h)
    {
        uint v = Convert.ToUInt32(h.TrimStart('#'), 16);
        return h.Length == 9 ? ColorHelper.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
                             : ColorHelper.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
