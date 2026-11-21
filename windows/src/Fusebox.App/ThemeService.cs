using Fusebox.Core.Look;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Fusebox.App;

/// <summary>
/// The resolved theme for every window (FEATURES.md §10.0): Themes.Resolve(theme, accent, textScale, font, light/dark, Low impact)
/// → brushes in Themes/Fusebox.xaml mutated in place → <see cref="ThemeChanged"/> (UI thread). Windows call <see cref="Attach"/>
/// once; themed windows get the theme background (or Mica for Liquid Glass), RequestedTheme and caption-button colors.
/// No Acrylic anywhere behind live numbers. Fonts and sizes: <see cref="Controls.Look"/>.
/// </summary>
public static class ThemeService
{
    sealed record Attached(WeakReference<Window> Window, bool Themed);
    static readonly List<Attached> windows = [];
    static readonly UISettings ui = new();
    static DispatcherQueue? queue;
    static bool pending;

    /// <summary>The theme now in use. Pages read colors from brushes, but numbers/fonts/row styles from here.</summary>
    public static Theme Current { get; private set; } = Themes.Instrument;

    /// <summary>Raised on the UI thread after the brushes changed. Re-apply fonts/sizes/row shapes here (brush colors update by themselves).</summary>
    public static event Action? ThemeChanged;

    /// <summary>False in Low impact or when Windows has animations off: then nothing animates (tab drop, page fade, fades).</summary>
    public static bool Motion => !LowImpact.On && ui.AnimationsEnabled;

    /// <summary>
    /// Registers a window. <paramref name="themed"/> = Fusebox theme background + colors (main window, tray flyout); false = plain
    /// system window that only follows the Light/Dark choice (Settings, Setup).
    /// </summary>
    public static void Attach(Window w, bool themed = true)
    {
        if (queue == null)
        {
            queue = DispatcherQueue.GetForCurrentThread();
            Prefs.Changed += k => { if (k is "theme" or "appearanceMode" or "accent" or "textScale" or "font") Post(); };
            LowImpact.Changed += Post;
            ui.ColorValuesChanged += (_, _) => Post();   // Windows light/dark or accent changed (arrives on a worker thread)
            Resolve();
        }
        windows.Add(new(new(w), themed));
        w.Closed += (_, _) => windows.RemoveAll(a => !a.Window.TryGetTarget(out var x) || x == w);
        Apply(w, themed);
    }

    static void Post()
    {
        if (pending || queue == null) return;
        pending = true;
        queue.TryEnqueue(DispatcherQueuePriority.Low, () => { pending = false; Resolve(); });
    }

    /// <summary>OS scheme for appearanceMode "system" (UISettings background is black in dark mode).</summary>
    public static Scheme SystemScheme
    {
        get { try { return ui.GetColorValue(UIColorType.Background).R < 128 ? Scheme.Dark : Scheme.Light; } catch { return Scheme.Dark; } }
    }

    /// <summary>The scheme the main window shows now (appearanceMode, or Windows' own for "system").</summary>
    public static Scheme Target => Prefs.Str("appearanceMode") switch { "light" => Scheme.Light, "dark" => Scheme.Dark, _ => SystemScheme };

    static void Resolve()
    {
        Argb accent;
        try { var c = ui.GetColorValue(UIColorType.Accent); accent = new(255, c.R, c.G, c.B); } catch { accent = new(255, 0, 120, 212); }
        Current = Themes.Resolve(Prefs.Str("theme"), Prefs.Str("accent"), Prefs.Double("textScale"), Prefs.Str("font"), Target, LowImpact.On, accent);
        try { UpdateBrushes(Current); } catch { }
        foreach (var a in windows.ToArray())
            if (a.Window.TryGetTarget(out var w)) Apply(w, a.Themed);
        ThemeChanged?.Invoke();
    }

    static void UpdateBrushes(Theme t)
    {
        void Set(string key, Argb c) { if (Find(key) is SolidColorBrush b) b.Color = C(c); }
        Set("FuseboxBg", t.Bg ?? Argb.Clear);
        Set("FuseboxText", t.Text); Set("FuseboxSecondary", t.Secondary); Set("FuseboxAccent", t.Accent);
        Set("FuseboxWarn", t.Warn); Set("FuseboxDanger", t.Danger); Set("FuseboxGood", t.Good);
        Set("FuseboxLine", t.Line); Set("FuseboxCardFill", t.Card);
        Set("FuseboxOnRow", t.OnRow()); Set("FuseboxOnRowSecondary", t.OnRow(true)); Set("FuseboxLabel", t.LabelColor);
        Set("FuseboxGraph", t.Graph); Set("FuseboxGraphFill", t.Graph.WithAlpha(0.15)); Set("FuseboxGraphSecond", t.Graph.WithAlpha(0.5));
        Set("FuseboxAccentSoft", t.Accent.WithAlpha(0.15)); Set("FuseboxHover", t.Secondary.WithAlpha(0.12));
        for (int i = 0; i < 5; i++) Set($"FuseboxBlock{i}", t.Block(i));
        Set("FuseboxCardBackground", t.Card); Set("FuseboxCardBorder", t.Line); Set("FuseboxGraphStroke", t.Graph);
        Set("FuseboxWarning", t.Warn); Set("FuseboxBad", t.Danger);
        var g = RowSpec.Glass(t.Scheme);
        if (Find("FuseboxGlassFill") is LinearGradientBrush fill && fill.GradientStops.Count == 2)
        { fill.GradientStops[0].Color = C(g.FillTop); fill.GradientStops[1].Color = C(g.FillBottom); }
        if (Find("FuseboxGlassRim") is LinearGradientBrush rim && rim.GradientStops.Count == 2)
        { rim.GradientStops[0].Color = C(g.RimTop); rim.GradientStops[1].Color = C(g.RimBottom); }
    }

    /// <summary>App resource by key (the indexer also searches the merged dictionaries); null when missing.</summary>
    static object? Find(string key)
    {
        try { return Application.Current.Resources[key]; } catch { return null; }
    }

    static void Apply(Window w, bool themed)
    {
        var t = Current;
        var scheme = themed ? t.Scheme : Target;
        try
        {
            // Liquid Glass = Mica (the only see-through theme); every other theme paints its own solid bg. Never Acrylic.
            bool mica = !themed || t.Bg == null;
            if (mica && w.SystemBackdrop is not MicaBackdrop) w.SystemBackdrop = new MicaBackdrop();
            else if (!mica && w.SystemBackdrop != null) w.SystemBackdrop = null;
        }
        catch { }
        if (w.Content is FrameworkElement root)
        {
            root.RequestedTheme = scheme == Scheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
            if (themed && root is Microsoft.UI.Xaml.Controls.Panel p)
                p.Background = t.Bg == null ? new SolidColorBrush(Colors.Transparent) : (Brush)Application.Current.Resources["FuseboxBg"];
        }
        try
        {
            var tb = w.AppWindow.TitleBar;
            var fg = themed ? C(t.Text) : (scheme == Scheme.Dark ? Colors.White : Colors.Black);
            tb.ButtonBackgroundColor = tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = tb.ButtonHoverForegroundColor = tb.ButtonPressedForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = themed ? C(t.Secondary) : fg;
            tb.ButtonHoverBackgroundColor = themed ? C(t.Secondary.WithAlpha(0.15)) : (Windows.UI.Color?)null;
            tb.ButtonPressedBackgroundColor = themed ? C(t.Secondary.WithAlpha(0.25)) : (Windows.UI.Color?)null;
        }
        catch { }
    }

    public static Windows.UI.Color C(Argb c) => ColorHelper.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>A brush from Themes/Fusebox.xaml by key (live: it follows theme changes).</summary>
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // ---- fonts (PLAN §7.8) ----

    static readonly Dictionary<FontFace, FontFamily> families = [];

    /// <summary>
    /// Segoe UI Variable · bundled Nunito (rounded) · Cascadia Mono → Consolas · Georgia · bundled Montserrat / Ubuntu.
    /// Bundled fonts are next to the exe (Assets\Fonts); the system fallback follows when the file can't be loaded.
    /// </summary>
    public static FontFamily Family(FontFace f)
    {
        if (families.TryGetValue(f, out var ff)) return ff;
        string name = f switch
        {
            FontFace.Rounded => "ms-appx:///Assets/Fonts/Nunito%5Bwght%5D.ttf#Nunito, Segoe UI Variable Text",
            FontFace.Mono => "Cascadia Mono, Consolas",
            FontFace.Serif => "Georgia",
            FontFace.Montserrat => "ms-appx:///Assets/Fonts/Montserrat%5Bwght%5D.ttf#Montserrat, Segoe UI Variable Text",
            FontFace.Ubuntu => "ms-appx:///Assets/Fonts/Ubuntu-Regular.ttf#Ubuntu, Segoe UI Variable Text",
            _ => "Segoe UI Variable Text",
        };
        return families[f] = new FontFamily(name);
    }

    /// <summary>Theme number weight (labels are always Medium).</summary>
    public static Windows.UI.Text.FontWeight NumberWeight => Current.Weight switch
    {
        Core.Look.NumberWeight.Bold => FontWeights.Bold, Core.Look.NumberWeight.Semibold => FontWeights.SemiBold, _ => FontWeights.Normal,
    };
}
