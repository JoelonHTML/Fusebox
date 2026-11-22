using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Fusebox.Core.Reports;

/// <summary>An RGB colour, 0..1 per channel.</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb Gray(double w) => new(w, w, w);
    /// <summary>This colour laid over white at <paramref name="alpha"/> (no transparency in the file: tints are mixed by hand).</summary>
    public Rgb Tint(double alpha) => new(1 - alpha * (1 - R), 1 - alpha * (1 - G), 1 - alpha * (1 - B));
}

/// <summary>
/// A tiny PDF 1.4 writer: A4 portrait pages (595 × 842 pt), the standard Helvetica / Helvetica-Bold fonts in WinAnsiEncoding (cp1252, so
/// nl/de/fr/es accents work; other symbols get a close stand-in or "?"), text, lines, (rounded) rectangles, polylines and filled polygons.
/// Coordinates are top-down (y = 0 at the top edge; text y = baseline). No images, no embedded fonts: a report is a few KB.
/// </summary>
public sealed class Pdf
{
    public const double Width = 595, Height = 842;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly List<StringBuilder> pages = [];
    readonly bool compress;
    StringBuilder page = null!;

    public Pdf(bool compress = true) { this.compress = compress; NewPage(); }

    public int PageCount => pages.Count;
    public void NewPage() { page = new StringBuilder(); pages.Add(page); }
    /// <summary>Draw on an earlier page (footers, the page-1 note).</summary>
    public void Select(int index) => page = pages[index];

    // ---- text ----

    public void Text(double x, double baseline, string s, double size, Rgb color, bool bold = false)
    {
        if (s.Length == 0) return;
        page.Append("BT /F").Append(bold ? '2' : '1').Append(' ').Append(N(size)).Append(" Tf ");
        Color(color, fill: true);
        page.Append(N(x)).Append(' ').Append(N(Height - baseline)).Append(" Td ").Append(Literal(s)).Append(" Tj ET\n");
    }

    public void TextRight(double right, double baseline, string s, double size, Rgb color, bool bold = false) =>
        Text(right - TextWidth(s, size, bold), baseline, s, size, color, bold);

    public void TextCenter(double center, double baseline, string s, double size, Rgb color, bool bold = false) =>
        Text(center - TextWidth(s, size, bold) / 2, baseline, s, size, color, bold);

    public static double TextWidth(string s, double size, bool bold = false)
    {
        var w = bold ? Bold : Regular;
        double sum = 0;
        foreach (var b in Encode(s)) if (b >= 32) sum += w[b - 32];
        return sum * size / 1000;
    }

    /// <summary>Greedy word wrap to <paramref name="max"/> points (keeps "\n"); a word longer than a line is shortened with "…".</summary>
    public static List<string> Wrap(string s, double size, double max, bool bold = false)
    {
        var lines = new List<string>();
        foreach (var para in s.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var longer = line.Length == 0 ? word : line + " " + word;
                if (TextWidth(longer, size, bold) <= max) { line = longer; continue; }
                if (line.Length > 0) lines.Add(line);
                line = Fit(word, size, max, bold);
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>The text, shortened with "…" when it is wider than <paramref name="max"/>.</summary>
    public static string Fit(string s, double size, double max, bool bold = false)
    {
        if (TextWidth(s, size, bold) <= max) return s;
        for (int n = s.Length - 1; n > 0; n--)
            if (TextWidth(s[..n] + "…", size, bold) <= max) return s[..n].TrimEnd() + "…";
        return "…";
    }

    // ---- shapes ----

    public void Line(double x1, double y1, double x2, double y2, Rgb color, double width = 0.5, bool dashed = false)
    {
        Color(color, fill: false);
        page.Append(N(width)).Append(" w ").Append(dashed ? "[2 2] 0 d " : "");
        Move(x1, y1); To(x2, y2);
        page.Append(dashed ? "S [] 0 d\n" : "S\n");
    }

    /// <summary>Rectangle with optional fill, stroke and corner radius (Bézier corners).</summary>
    public void Rect(double x, double y, double w, double h, Rgb? fill, Rgb? stroke = null, double radius = 0, double lineWidth = 0.5)
    {
        if (w <= 0 || h <= 0 || (fill == null && stroke == null)) return;
        if (fill is { } f) Color(f, fill: true);
        if (stroke is { } s) { Color(s, fill: false); page.Append(N(lineWidth)).Append(" w "); }
        double r = Math.Min(radius, Math.Min(w, h) / 2);
        if (r <= 0) page.Append(N(x)).Append(' ').Append(N(Height - y - h)).Append(' ').Append(N(w)).Append(' ').Append(N(h)).Append(" re ");
        else
        {
            double k = r * 0.5523, b = y + h, rt = x + w;
            Move(x + r, y); To(rt - r, y); Curve(rt - r + k, y, rt, y + r - k, rt, y + r);
            To(rt, b - r); Curve(rt, b - r + k, rt - r + k, b, rt - r, b);
            To(x + r, b); Curve(x + r - k, b, x, b - r + k, x, b - r);
            To(x, y + r); Curve(x, y + r - k, x + r - k, y, x + r, y);
            page.Append("h ");
        }
        page.Append(fill != null && stroke != null ? "B\n" : fill != null ? "f\n" : "S\n");
    }

    public void Dot(double cx, double cy, double r, Rgb fill) => Rect(cx - r, cy - r, 2 * r, 2 * r, fill, radius: r);

    /// <summary>Open polyline through the points (needs 2 or more).</summary>
    public void Polyline(IReadOnlyList<(double X, double Y)> pts, Rgb color, double width = 1, bool dashed = false)
    {
        if (pts.Count < 2) return;
        Color(color, fill: false);
        page.Append(N(width)).Append(" w 1 j 1 J ").Append(dashed ? "[3 2] 0 d " : "");
        Move(pts[0].X, pts[0].Y);
        for (int i = 1; i < pts.Count; i++) To(pts[i].X, pts[i].Y);
        page.Append(dashed ? "S [] 0 d 0 j 0 J\n" : "S 0 j 0 J\n");
    }

    /// <summary>Filled closed polygon (the area under a graph line).</summary>
    public void Polygon(IReadOnlyList<(double X, double Y)> pts, Rgb fill)
    {
        if (pts.Count < 3) return;
        Color(fill, fill: true);
        Move(pts[0].X, pts[0].Y);
        for (int i = 1; i < pts.Count; i++) To(pts[i].X, pts[i].Y);
        page.Append("h f\n");
    }

    void Move(double x, double y) => page.Append(N(x)).Append(' ').Append(N(Height - y)).Append(" m ");
    void To(double x, double y) => page.Append(N(x)).Append(' ').Append(N(Height - y)).Append(" l ");
    void Curve(double x1, double y1, double x2, double y2, double x3, double y3) =>
        page.Append(N(x1)).Append(' ').Append(N(Height - y1)).Append(' ').Append(N(x2)).Append(' ').Append(N(Height - y2)).Append(' ')
            .Append(N(x3)).Append(' ').Append(N(Height - y3)).Append(" c ");
    void Color(Rgb c, bool fill) =>
        page.Append(N(c.R)).Append(' ').Append(N(c.G)).Append(' ').Append(N(c.B)).Append(fill ? " rg " : " RG ");

    static string N(double v) => double.IsFinite(v) ? Math.Round(v, 2).ToString("0.##", Inv) : "0";

    // ---- file ----

    /// <summary>The finished file: catalog, page tree, two fonts, info, one content stream per page, xref table and trailer.</summary>
    public byte[] Save(string title = "Fusebox")
    {
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void Raw(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void Obj(string body) { offsets.Add(ms.Position); Raw($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }

        Raw("%PDF-1.4\n");
        ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);   // marks the file as binary for old tools
        int n = pages.Count;
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{6 + 2 * i} 0 R"))}] /Count {n} /MediaBox [0 0 {Width} {Height}] >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        Obj($"<< /Title {Literal(title)} /Producer (Fusebox) >>");
        for (int i = 0; i < n; i++)
        {
            Obj($"<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {7 + 2 * i} 0 R >>");
            var data = Encoding.ASCII.GetBytes(pages[i].ToString());
            if (compress)
            {
                var z = new MemoryStream();
                using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(data);
                data = z.ToArray();
            }
            offsets.Add(ms.Position);
            Raw($"{offsets.Count} 0 obj\n<< /Length {data.Length}{(compress ? " /Filter /FlateDecode" : "")} >>\nstream\n");
            ms.Write(data);
            Raw("\nendstream\nendobj\n");
        }
        long xref = ms.Position;
        var sb = new StringBuilder($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10", Inv)).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Raw(sb.ToString());
        return ms.ToArray();
    }

    /// <summary>"(…)" string literal: cp1252 bytes, with ( ) \ escaped and everything outside printable ASCII as octal, so streams stay ASCII.</summary>
    static string Literal(string s)
    {
        var sb = new StringBuilder("(");
        foreach (var b in Encode(s))
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
            else if (b is < 32 or > 126) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else sb.Append((char)b);
        }
        return sb.Append(')').ToString();
    }

    // ---- cp1252 ----

    /// <summary>cp1252 bytes 0x80–0x9F ('\0' = unused).</summary>
    const string High = "€\0‚ƒ„…†‡ˆ‰Š‹Œ\0Ž\0\0‘’“”•–—˜™š›œ\0žŸ";

    /// <summary>Text → WinAnsi (cp1252) bytes. Common symbols outside cp1252 get a stand-in, anything else "?"; control characters are dropped.</summary>
    public static byte[] Encode(string s)
    {
        var o = new List<byte>(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            int c = r.Value;
            if (c is >= 32 and < 127 or >= 160 and <= 255) { o.Add((byte)c); continue; }
            int hi = c is > 0 and < 0x10000 ? High.IndexOf((char)c) : -1;
            if (hi >= 0) { o.Add((byte)(0x80 + hi)); continue; }
            string sub = c switch
            {
                '\t' => " ", 0x2212 or 0x2010 or 0x2011 or 0x2500 or 0x2504 or 0x2508 => "-", 0x2265 => ">=", 0x2264 => "<=",
                0x2192 => "->", 0x2190 => "<-", 0x2248 => "~", 0x2009 or 0x202F or 0x2007 or 0x200A => " ", 0x2032 => "'", 0x2033 => "\"",
                0x2713 or 0x2714 => "v", 0x2717 => "x", 0x22C5 or 0x2219 => "·", < 32 => "", _ => "?",
            };
            foreach (var ch in sub) o.Add((byte)ch);
        }
        return o.ToArray();
    }

    // Helvetica / Helvetica-Bold advance widths (Adobe AFM, 1/1000 em) for WinAnsi codes 32–255.
    static readonly short[] Regular =
    [
        278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,
        584,584,584,556,1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,
        667,667,611,278,278,278,469,556,333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,
        278,556,500,722,500,500,500,334,260,334,584,350,556,350,222,556,333,1000,556,556,333,1000,667,333,1000,350,611,350,
        350,222,222,333,333,350,556,1000,333,1000,500,333,944,350,500,667,278,333,556,556,556,556,260,556,333,737,370,556,
        584,333,737,333,400,584,333,333,333,556,537,278,333,333,365,556,834,834,834,611,667,667,667,667,667,667,1000,722,
        667,667,667,667,278,278,278,278,722,722,778,778,778,778,778,584,778,722,722,722,722,667,667,611,556,556,556,556,
        556,556,889,500,556,556,556,556,278,278,278,278,556,556,556,556,556,556,556,584,611,556,556,556,556,500,556,500,
    ];
    static readonly short[] Bold =
    [
        278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,333,333,
        584,584,584,611,975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,611,722,667,944,
        667,667,611,333,278,333,584,556,333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,611,611,389,556,
        333,611,556,778,556,556,500,389,280,389,584,350,556,350,278,556,500,1000,556,556,333,1000,667,333,1000,350,611,350,
        350,278,278,500,500,350,556,1000,333,1000,556,333,944,350,500,667,278,333,556,556,556,556,280,556,333,737,370,556,
        584,333,737,333,400,584,333,333,333,611,556,278,333,333,365,556,834,834,834,611,722,722,722,722,722,722,1000,722,
        667,667,667,667,278,278,278,278,722,722,778,778,778,778,778,584,778,722,722,722,722,667,667,611,556,556,556,556,
        556,556,889,556,556,556,556,556,278,278,278,278,611,611,611,611,611,611,611,584,611,611,611,611,611,556,611,556,
    ];
}
