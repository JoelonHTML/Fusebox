using System.Globalization;

namespace Fusebox.Core.Look;

/// <summary>sRGB color with alpha, 0–255 per channel.</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    static byte C(double v) => (byte)Math.Clamp(Math.Round(v * 255, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>From 0–1 floats, like SwiftUI Color(red:green:blue:) (rounded ×255).</summary>
    public static Argb Rgb(double r, double g, double b, double a = 1) => new(C(a), C(r), C(g), C(b));
    /// <summary>Color(white:) with optional opacity.</summary>
    public static Argb White(double w, double a = 1) => Rgb(w, w, w, a);
    public static readonly Argb Clear = new(0, 0, 0, 0);

    public Argb WithAlpha(double a) => this with { A = C(a * A / 255.0) };

    /// <summary>Mac Color(hex:): exactly 6 hex digits, with or without '#'; anything else = null (keep the default).</summary>
    public static Argb? FromHex(string? s)
    {
        if (s == null) return null;
        var h = s.StartsWith('#') ? s[1..] : s;
        if (h.Length != 6 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return new(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>"#RRGGBB" uppercase (the stored accent format; alpha dropped).</summary>
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>WCAG relative luminance (0 black … 1 white) of the sRGB channels.</summary>
    public double Luminance
    {
        get
        {
            static double L(byte c) { double v = c / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
            return 0.2126 * L(R) + 0.7152 * L(G) + 0.0722 * L(B);
        }
    }

    /// <summary>Hue 0–1, saturation 0–1, brightness 0–1 (HSB/HSV like NSColor).</summary>
    public (double H, double S, double V) Hsb
    {
        get
        {
            double r = R / 255.0, g = G / 255.0, b = B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double h = d == 0 ? 0 : max == r ? ((g - b) / d % 6 + 6) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            return (h / 6, max == 0 ? 0 : d / max, max);
        }
    }

    public static Argb FromHsb(double h, double s, double v, byte a = 255)
    {
        h = (h % 1 + 1) % 1 * 6;
        int i = (int)Math.Floor(h);
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return Rgb(r, g, b) with { A = a };
    }
}

public enum Scheme { Light, Dark }
public enum RowStyle { Lines, Plain, Cards, Blocks, Glass }
public enum FontDesign { Default, Rounded, Mono }
public enum NumberWeight { Regular, Semibold, Bold }

/// <summary>Which font family a text uses on Windows (PLAN §7.8). The App maps these to FontFamily objects.</summary>
public enum FontFace { Segoe, Rounded, Mono, Serif, Montserrat, Ubuntu }

/// <summary>
/// One theme with its tokens (FEATURES.md §10.1). The six built-in ones are in <see cref="Themes.All"/>; a page always works with
/// the resolved one from <see cref="Themes.Resolve"/> (accent, scale, font, light/dark, Low impact already applied).
/// </summary>
public sealed record Theme(
    string Id, string Name, Argb? Bg, Argb Text, Argb Secondary, Argb Accent, Argb Warn, Argb Danger, Argb Good,
    Argb Line, Argb Card, RowStyle RowStyle, FontDesign Design, NumberWeight Weight, Argb[] Blocks, Scheme? Designed)
{
    /// <summary>The scheme this theme is shown in now (set by Resolve; the designed one before that).</summary>
    public Scheme Scheme { get; init; } = Designed ?? Scheme.Dark;
    public double Scale { get; init; } = 1;
    /// <summary>Font pref id (style, sfPro, rounded, mono, serif, montserrat, ubuntu).</summary>
    public string Font { get; init; } = "style";
    /// <summary>True after Low impact made it solid (only changes Liquid Glass).</summary>
    public bool Solid { get; init; }

    public bool IsDark => Scheme == Scheme.Dark;

    /// <summary>Text color on a row: Color blocks always white (secondary white 80 %), else text / secondary.</summary>
    public Argb OnRow(bool secondary = false) => RowStyle == RowStyle.Blocks ? Argb.White(1, secondary ? 0.8 : 1) : secondary ? Secondary : Text;

    /// <summary>Metric labels (CPU, RAM …) on Live: accent in Instrument only, else secondary-on-row.</summary>
    public Argb LabelColor => Id == "instrument" ? Accent : OnRow(true);

    /// <summary>Graph line, core bars, sparklines: accent, white on Color blocks.</summary>
    public Argb Graph => RowStyle == RowStyle.Blocks ? Argb.White(1) : Accent;

    /// <summary>Value color for level 0/1/2 (blocks: always white, warnings are told by text).</summary>
    public Argb Level(int level) => RowStyle == RowStyle.Blocks ? OnRow() : level >= 2 ? Danger : level == 1 ? Warn : OnRow();

    /// <summary>Block fill for a row index (Color blocks only).</summary>
    public Argb Block(int index) => Blocks.Length == 0 ? Card : Blocks[(index % Blocks.Length + Blocks.Length) % Blocks.Length];

    /// <summary>Extra vertical space each row takes on Live (layout math, §3.2).</summary>
    public double RowPad => RowSpec.For(RowStyle).RowPad;

    /// <summary>Font family for numbers and labels.</summary>
    public FontFace Face => Font switch
    {
        "sfPro" => FontFace.Segoe, "rounded" => FontFace.Rounded, "mono" => FontFace.Mono, "serif" => FontFace.Serif,
        "montserrat" => FontFace.Montserrat, "ubuntu" => FontFace.Ubuntu,
        _ => Design switch { FontDesign.Rounded => FontFace.Rounded, FontDesign.Mono => FontFace.Mono, _ => FontFace.Segoe },
    };

    /// <summary>Theme size × text scale (theme.number(size) / theme.label(size) on the Mac).</summary>
    public double Size(double pt) => pt * Scale;

    /// <summary>Mac theme equality used to decide a re-render.</summary>
    public bool SameLook(Theme o) => Id == o.Id && Accent == o.Accent && Scale == o.Scale && Font == o.Font && RowStyle == o.RowStyle
        && Scheme == o.Scheme && Bg == o.Bg && Solid == o.Solid;
}

/// <summary>Row container per row style (§10.5). Radii are the Mac's; generic Windows cards (not themed rows) use 8.</summary>
public sealed record RowSpec(double PadH, double PadV, double Radius, double RowPad, bool Fill, bool Border, bool BottomLine)
{
    public static RowSpec For(RowStyle s) => s switch
    {
        RowStyle.Lines => new(0, 7, 0, 15, false, false, true),
        RowStyle.Plain => new(0, 5, 0, 10, false, false, false),
        RowStyle.Cards => new(10, 8, 12, 22, true, true, false),
        RowStyle.Blocks => new(10, 8, 10, 22, true, false, false),
        _ => new(10, 8, 14, 22, true, true, false),   // glass: gradient fill + gradient rim
    };

    /// <summary>Glass row gradients (top → bottom): fill and 1-pt rim, per scheme.</summary>
    public static (Argb FillTop, Argb FillBottom, Argb RimTop, Argb RimBottom) Glass(Scheme s) => s == Scheme.Dark
        ? (Argb.White(1, 0.11), Argb.White(1, 0.05), Argb.White(1, 0.28), Argb.White(1, 0.06))
        : (Argb.White(1, 0.62), Argb.White(1, 0.38), Argb.White(1, 0.95), Argb.White(0, 0.06));
}

/// <summary>The six themes with the Mac's exact values (Theme.swift) and the resolve pipeline (§10.0).</summary>
public static class Themes
{
    static Argb Rgb(double r, double g, double b) => Argb.Rgb(r, g, b);
    static Argb W(double w, double a = 1) => Argb.White(w, a);

    public static readonly Theme Instrument = new("instrument", "Instrument",
        Rgb(0.122, 0.169, 0.239), Rgb(0.95, 0.96, 0.97), Rgb(0.60, 0.65, 0.72), Rgb(0.96, 0.72, 0.24),
        Rgb(1.0, 0.60, 0.25), Rgb(1.0, 0.38, 0.35), Rgb(0.35, 0.85, 0.6), Rgb(0.17, 0.23, 0.31), Rgb(0.15, 0.20, 0.28),
        RowStyle.Lines, FontDesign.Rounded, NumberWeight.Semibold, [], Scheme.Dark);

    public static readonly Theme Terminal = new("terminal", "Terminal",
        Rgb(0.04, 0.06, 0.05), Rgb(0.49, 0.99, 0.60), Rgb(0.25, 0.69, 0.37), Rgb(0.49, 0.99, 0.60),
        Rgb(1.0, 0.80, 0.25), Rgb(1.0, 0.35, 0.35), Rgb(0.49, 0.99, 0.60), Rgb(0.11, 0.16, 0.13), Rgb(0.07, 0.10, 0.08),
        RowStyle.Plain, FontDesign.Mono, NumberWeight.Regular, [], Scheme.Dark);

    public static readonly Theme Minimal = new("minimal", "Minimal light",
        W(1), W(0.07), W(0.36), Rgb(0.0, 0.42, 0.85),
        Rgb(0.69, 0.33, 0.0), Rgb(0.77, 0.12, 0.12), Rgb(0.05, 0.45, 0.24), W(0.9), W(0.97),
        RowStyle.Lines, FontDesign.Default, NumberWeight.Regular, [], Scheme.Light);

    public static readonly Theme Glass = new("glas", "Glass",
        Rgb(0.36, 0.29, 0.84), W(1), W(1, 0.78), W(1),
        Rgb(1.0, 0.85, 0.4), Rgb(1.0, 0.55, 0.55), W(1), W(1, 0.35), W(1, 0.16),
        RowStyle.Cards, FontDesign.Rounded, NumberWeight.Semibold, [], Scheme.Dark);

    public static readonly Theme Blocks = new("blokken", "Color blocks",
        Rgb(0.96, 0.95, 0.92), W(0.1), Rgb(0.42, 0.40, 0.37), Rgb(0.18, 0.49, 0.84),
        Rgb(0.95, 0.55, 0.1), Rgb(0.85, 0.2, 0.2), Rgb(0.11, 0.62, 0.46), Rgb(0.85, 0.83, 0.77), W(1),
        RowStyle.Blocks, FontDesign.Rounded, NumberWeight.Bold,
        [Rgb(0.88, 0.34, 0.24), Rgb(0.18, 0.49, 0.84), Rgb(0.11, 0.62, 0.46), Rgb(0.48, 0.33, 0.78), Rgb(0.85, 0.6, 0.1)], Scheme.Light);

    /// <summary>Liquid Glass follows the OS: its system colors are filled in by <see cref="Resolve"/> (Fluent text/fill colors).</summary>
    public static readonly Theme Liquid = new("liquid", "Liquid Glass",
        null, W(0, 0.894), W(0, 0.62), new(255, 0, 95, 184), new(255, 157, 93, 0), new(255, 196, 43, 28), new(255, 15, 123, 15),
        W(0, 0.1), Argb.Clear, RowStyle.Glass, FontDesign.Rounded, NumberWeight.Semibold, [], null);

    /// <summary>Picker order: Instrument, Terminal, Minimal light, Glass, Color blocks, Liquid Glass.</summary>
    public static readonly IReadOnlyList<Theme> All = [Instrument, Terminal, Minimal, Glass, Blocks, Liquid];

    /// <summary>Unknown id → Instrument.</summary>
    public static Theme Named(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Instrument;

    /// <summary>
    /// The pipeline of §10.0, in order: theme by id → accent overlay ("#RRGGBB", "" = keep) → text scale (clamped 0.8–1.6) → font id
    /// → light/dark conversion (§10.3) → accent legibility (§10.4) → Low impact solid (§10.6).
    /// </summary>
    /// <param name="target">Light/Dark from appearanceMode, or the OS scheme for "system".</param>
    /// <param name="systemAccent">Windows accent color (Liquid Glass uses it as its accent).</param>
    public static Theme Resolve(string? id, string? accent, double textScale, string? font, Scheme target, bool lowImpact, Argb systemAccent)
    {
        var t = Named(id);
        if (t.Designed == null) t = SystemColors(t, target, systemAccent);
        if (Argb.FromHex(accent) is { } a) t = t with { Accent = a };
        t = t with
        {
            Scale = double.IsFinite(textScale) ? Math.Clamp(textScale, 0.8, 1.6) : 1,
            Font = font is "sfPro" or "rounded" or "mono" or "serif" or "montserrat" or "ubuntu" ? font : "style",
        };
        t = Adapt(Convert(t, target), target);
        return lowImpact ? MakeSolid(t) : t;
    }

    /// <summary>Liquid Glass tokens for the OS scheme (Fluent TextFillColor*/SystemFillColor*).</summary>
    static Theme SystemColors(Theme t, Scheme s, Argb accent) => s == Scheme.Dark
        ? t with { Text = W(1), Secondary = W(1, 0.773), Accent = accent, Warn = new(255, 252, 225, 0), Danger = new(255, 255, 153, 164),
                   Good = new(255, 108, 203, 95), Line = W(1, 0.1), Scheme = s }
        : t with { Text = W(0, 0.894), Secondary = W(0, 0.62), Accent = accent, Warn = new(255, 157, 93, 0), Danger = new(255, 196, 43, 28),
                   Good = new(255, 15, 123, 15), Line = W(0, 0.1), Scheme = s };

    /// <summary>§10.3: a theme designed for the other scheme gets neutral greys; accent, warn/danger/good, row style, fonts stay.</summary>
    public static Theme Convert(Theme t, Scheme s)
    {
        if (t.Designed is not { } own || own == s) return t with { Scheme = s };
        bool dark = s == Scheme.Dark;
        var c = t with
        {
            Bg = dark ? W(0.11) : W(1), Text = dark ? W(0.95) : W(0.08), Secondary = dark ? W(0.62) : W(0.45),
            Line = dark ? W(0.22) : W(0.9), Card = dark ? W(0.16) : W(0.96), Scheme = s,
        };
        if (!dark && t.Id == "glas") c = c with { Accent = W(0.08) };   // white accent is unreadable on white
        return c;
    }

    /// <summary>§10.4: dark accent on dark → brighter; light accent on light → darker (not for Color blocks).</summary>
    public static Theme Adapt(Theme t, Scheme s)
    {
        var c = t.Accent;
        double l = c.Luminance;
        var (h, sat, _) = c.Hsb;
        if (s == Scheme.Dark && l < 0.3) return t with { Accent = Argb.FromHsb(h, sat * 0.6, 1, c.A) };
        if (s == Scheme.Light && l > 0.6 && t.RowStyle != RowStyle.Blocks) return t with { Accent = Argb.FromHsb(h, Math.Min(sat * 1.3 + 0.2, 1), 0.55, c.A) };
        return t;
    }

    /// <summary>§10.6 Low impact: only Liquid Glass changes: plain window background, 5 % cards with a border, no glass.</summary>
    public static Theme MakeSolid(Theme t)
    {
        if (t.RowStyle != RowStyle.Glass) return t;
        bool dark = t.IsDark;
        return t with { Bg = dark ? new Argb(255, 32, 32, 32) : new Argb(255, 243, 243, 243), Card = t.Text.WithAlpha(0.05), RowStyle = RowStyle.Cards, Solid = true };
    }
}
