using System.Globalization;
using System.Text;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Docked;

// The Docked page as plain data (spec §7): the page only draws these records, the builder (DockedBuilder) fills them.
// Everything here is text and flags, so it is unit-testable and has a stable Signature for "redraw only when changed".

public enum NoteLevel { Good, Info, Warn }
public enum Conn { None, Pass, Tee, Elbow }

public sealed record Note(NoteLevel Level, string Text);
public sealed record Fact(string Key, string Value);
public sealed record FactGroup(string Title, string Glyph, IReadOnlyList<Fact> Facts);

/// <summary>Right-hand number: [Prefix small] Big [Suffix small], warn colour + down arrow when Slow, Glyph = leading icon (battery).</summary>
public sealed record RightValue(string? Prefix, string Big, string? Suffix, bool Slow, string? Glyph = null);

public sealed record Detail(string Title, string Glyph, RightValue? Big, IReadOnlyList<Note> Notes, IReadOnlyList<FactGroup> Groups, IReadOnlyList<(string Key, string Value)> Raw);

public sealed record TreeRow(string Id, IReadOnlyList<Conn> Cols, string Glyph, string Title, bool Dim, bool Port, string? Info,
    IReadOnlyList<string> Facts, RightValue? Right, string? Warning, Detail? Detail);

public sealed record Group(string Id, IReadOnlyList<TreeRow> Rows);

public sealed record Chip(string Glyph, string Value, string Label, string? Tip);

/// <summary>Header card: Big (+ Unit, Caption) = watts into the PC when Windows reports a charge rate, else just the state.</summary>
public sealed record Header(string Big, string? Unit, string? Caption, string? Percent, string? BatteryText, IReadOnlyList<Chip> Chips, string? Footnote);

public sealed record DockedView(Header Header, IReadOnlyList<Group> Ports, string? EmptyLine, IReadOnlyList<Group> Wireless,
    IReadOnlyList<TreeRow> Paired, Group? Other, string? EmptyState, string Signature)
{
    public IEnumerable<TreeRow> AllRows => Ports.Concat(Wireless).SelectMany(g => g.Rows).Concat(Paired).Concat(Other?.Rows ?? []);
}

public sealed record DockedOptions(bool Internals = false, bool Empty = true, bool Displays = true, bool Wireless = true, bool Bytes = false);

/// <summary>Number and text formatting shared by rows and details (spec §7.8).</summary>
public static class DockedText
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Round to 1 decimal, drop ".0": 16.97 gives "17", 17.53 gives "17.5".</summary>
    public static string Trim(double x)
    {
        var r = Math.Round(x, 1, MidpointRounding.AwayFromZero);
        return r == Math.Floor(r) ? ((long)r).ToString(Inv) : r.ToString("0.0", Inv);
    }

    /// <summary>("10", "Gbit/s"); with bytes: ("1.3", "GB/s").</summary>
    public static (string Value, string Unit) SpeedParts(double bps, bool bytes)
    {
        double v = bytes ? bps / 8 : bps;
        return v >= 1e9 ? (Trim(v / 1e9), bytes ? "GB/s" : "Gbit/s") : (Trim(v / 1e6), bytes ? "MB/s" : "Mbit/s");
    }

    public static string Speed(double bps, bool bytes) { var (v, u) = SpeedParts(bps, bytes); return v + " " + u; }

    /// <summary>"480 Mbit/s" now only, "10 / 40 Gbit/s" same unit, "480 Mbit/s of 10 Gbit/s" different units; slow marks the down arrow.</summary>
    public static RightValue Link(long now, long max, bool slow, bool bytes)
    {
        var (nv, nu) = SpeedParts(now, bytes);
        if (max <= now) return new(null, nv, nu, false);
        var (mv, mu) = SpeedParts(max, bytes);
        return new(null, nv, nu == mu ? $"/ {mv} {mu}" : L("%@ of %@ %@", nu, mv, mu), slow);
    }

    public static RightValue Hertz(double now, double max)
    {
        bool slow = max > now + 1;
        return new(null, Trim(now), slow ? $"/ {Trim(max)} Hz" : "Hz", slow);
    }

    public static string BatteryGlyph(int pct) => pct >= 100 ? "" : ((char)(0xE850 + Math.Clamp(pct, 0, 99) / 10)).ToString();

    /// <summary>"4K60", "1440p75", or "{w} × {h} at {hz} Hz".</summary>
    public static string ModeName(int w, int h, double hz)
    {
        var f = Math.Round(hz).ToString(Inv);
        return (w, h) switch
        {
            (3840 or 4096, 2160) => "4K" + f,
            (5120, 2880) => "5K" + f,
            (6016, 3384) => "6K" + f,
            (2560, 1440) => "1440p" + f,
            (1920, 1080) => "1080p" + f,
            _ => $"{w} × {h} at {f} Hz",
        };
    }

    public static string BatteryText(PowerInfo p)
    {
        if (p.OnAc == true && p.Percent >= 100 && !p.Charging) return L("battery full");
        if (p.Charging) return L("charging") + (p.Minutes is > 0 ? L(" · full in %@", Fmt.Duration(p.Minutes.Value)) : "");
        if (p.OnAc == true) return L("not charging");
        return p.Minutes is > 0 ? L("%@ left", Fmt.Duration(p.Minutes.Value)) : L("on battery");
    }
}

/// <summary>Stable text form of a view: two views with the same signature draw the same, so the page skips the redraw.</summary>
static class ViewSig
{
    public static string Of(Header h, IEnumerable<TreeRow> rows, string? empty, string? emptyLine)
    {
        var sb = new StringBuilder();
        sb.Append(h.Big).Append('|').Append(h.Unit).Append('|').Append(h.Caption).Append('|').Append(h.Percent).Append('|').Append(h.BatteryText).Append('|').Append(h.Footnote).Append('\n');
        foreach (var c in h.Chips) sb.Append(c.Value).Append(c.Label).Append(c.Tip).Append(';');
        sb.Append('\n').Append(empty).Append('|').Append(emptyLine).Append('\n');
        foreach (var r in rows)
        {
            sb.Append(r.Id).Append('|').Append(string.Concat(r.Cols.Select(c => (char)('0' + (int)c)))).Append('|').Append(r.Glyph).Append(r.Title).Append('|').Append(r.Dim).Append(r.Port).Append(r.Info)
              .Append('|').Append(string.Join('•', r.Facts)).Append('|').Append(Rv(r.Right)).Append('|').Append(r.Warning).Append('|');
            if (r.Detail is { } d)
            {
                sb.Append(d.Title).Append(Rv(d.Big));
                foreach (var n in d.Notes) sb.Append((int)n.Level).Append(n.Text);
                foreach (var g in d.Groups) { sb.Append(g.Title); foreach (var f in g.Facts) sb.Append(f.Key).Append('=').Append(f.Value).Append(','); }
                foreach (var (k, v) in d.Raw) sb.Append(k).Append('=').Append(v).Append(',');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string Rv(RightValue? r) => r == null ? "" : $"{r.Prefix}/{r.Big}/{r.Suffix}/{r.Slow}/{r.Glyph}";
}
