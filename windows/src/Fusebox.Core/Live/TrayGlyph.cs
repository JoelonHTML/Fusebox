namespace Fusebox.Core.Live;

/// <summary>One metric in the tray icon: its text value ("23%", "↓1.2 MB/s") and fraction 0…1 now, plus the last 30 fractions (graph style).</summary>
public sealed record TrayItem(string Abbr, string Text, double Fraction, IReadOnlyList<double> History);

/// <summary>Texts and fractions for the tray icon and tooltip (§8.2).</summary>
public static class TrayInfo
{
    const double GiB = 1073741824;

    /// <summary>menuValue: NET "↓1.2 MB/s", I/O short(read+write), CPU/RAM/SSD with a unit when absolute, else the Live percentage.</summary>
    public static string MenuValue(Metric m, LiveSample s, LiveOptions o)
    {
        switch (m)
        {
            case Metric.Net: return double.IsNaN(s.NetIn) ? "–" : "↓" + Fmt.Short(s.NetIn, o.NetBits);
            case Metric.Io: return double.IsNaN(s.DiskRead) ? "–" : Fmt.Short(Metrics.Value(m, s), o.NetBits);
            case Metric.Cpu when o.IsAbsolute(m) && o.CpuCount > 0: return Fmt.One(s.Cpu / 100 * o.CpuCount) + " cores";
            case Metric.Ram when o.IsAbsolute(m): return Fmt.One(s.RamUsed / GiB) + " GB";
            case Metric.Ssd when o.IsAbsolute(m): return double.IsNaN(s.DiskFree) ? "–" : $"{(long)(s.DiskFree / 1e9)} GB";
            default:
                var c = LiveCards.Card(m, s, o);
                return c.Value.Length == 0 ? "–" : c.Value;
        }
    }

    /// <summary>Bars and graphs: CPU/GPU/RAM/BAT percent ÷ 100, NET and I/O ÷ 100 MB/s, SSD used.</summary>
    public static double Fraction(Metric m, LiveSample s)
    {
        double v = Metrics.Value(m, s);
        if (!double.IsFinite(v)) return 0;
        return Math.Clamp(m is Metric.Net or Metric.Io ? v / 100e6 : v / 100, 0, 1);
    }

    /// <summary>The first <paramref name="max"/> metrics of the pref that exist on this PC, in order.</summary>
    public static List<Metric> Pick(string pref, Func<Metric, bool> available, int max = 4) =>
        Metrics.Parse(pref).Where(available).Take(max).ToList();

    /// <summary>Tooltip / "text" style: "CPU 23%  RAM 12.0 GB  NET ↓1.2 MB/s" (two spaces), cut to the 127 characters a tray tip can hold.</summary>
    public static string Tooltip(IEnumerable<TrayItem> items, string prefix = "")
    {
        var t = prefix + string.Join("  ", items.Select(i => $"{i.Abbr} {i.Text}"));
        return t.Length <= 127 ? t : t[..126] + "…";
    }
}

/// <summary>
/// Draws the tray icon styles into an ARGB pixel buffer (straight alpha), size × size (16 at 100 % scale, 20/24 above). Pure: the App
/// turns the buffer into an HICON only when it differs from the last one. The tray can't show text, so numbers are drawn with a 3×5 pixel
/// font; the tooltip carries the full text. Glyph colour: white on a dark taskbar, near-black on a light one.
/// </summary>
public static class TrayGlyph
{
    static readonly string[] Digits =
    [
        "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111",
    ];

    public static uint Ink(bool lightTaskbar) => lightTaskbar ? 0xFF202020u : 0xFFFFFFFFu;

    /// <param name="style">text · compact · bars · graph · icon</param>
    /// <param name="alert">icon style: the status is not green</param>
    public static uint[] Render(string style, int size, bool lightTaskbar, IReadOnlyList<TrayItem> items, bool alert = false)
    {
        var px = new uint[size * size];
        uint ink = Ink(lightTaskbar);
        switch (style)
        {
            case "bars": Bars(px, size, ink, items); break;
            case "graph": Graphs(px, size, ink, items); break;
            case "compact": Compact(px, size, ink, items); break;
            case "icon": Gauge(px, size, ink, alert); break;
            default: Text(px, size, ink, items); break;
        }
        return px;
    }

    static void Put(uint[] px, int size, int x, int y, uint color, double cover = 1)
    {
        if (x < 0 || y < 0 || x >= size || y >= size || cover <= 0) return;
        uint a = (uint)Math.Round(Math.Clamp(cover, 0, 1) * (color >> 24));
        uint old = px[y * size + x] >> 24;
        if (a < old) return;
        px[y * size + x] = (a << 24) | (color & 0xFFFFFF);
    }

    /// <summary>The integer part of a value text, at most 3 digits ("12.4 GB" → "12", "↓1.2 MB/s" → "1", "–" → "").</summary>
    public static string Number(string text)
    {
        int i = 0;
        while (i < text.Length && !char.IsAsciiDigit(text[i])) i++;
        int j = i;
        while (j < text.Length && char.IsAsciiDigit(text[j])) j++;
        var d = text[i..j];
        return d.Length > 3 ? d[^3..] : d;
    }

    static int DrawDigits(uint[] px, int size, int x, int y, string digits, int scale, uint ink)
    {
        foreach (var ch in digits)
        {
            var g = Digits[ch - '0'];
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 3; c++)
                    if (g[r * 3 + c] == '1')
                        for (int dy = 0; dy < scale; dy++) for (int dx = 0; dx < scale; dx++) Put(px, size, x + c * scale + dx, y + r * scale + dy, ink);
            x += 4 * scale;
        }
        return x;
    }

    static int Width(string digits, int scale) => digits.Length == 0 ? 0 : (digits.Length * 4 - 1) * scale;

    /// <summary>One big number (first metric), as large as fits: two digits at 2×, three at 1×, at 24 px 3×/2×.</summary>
    static void Text(uint[] px, int size, uint ink, IReadOnlyList<TrayItem> items)
    {
        var d = items.Count > 0 ? Number(items[0].Text) : "";
        if (d.Length == 0) { Put(px, size, size / 2 - 1, size / 2, ink); Put(px, size, size / 2, size / 2, ink); return; }
        int scale = Math.Max(1, Math.Min((size - 2) / (d.Length * 4 - 1), (size - 2) / 5));
        DrawDigits(px, size, (size - Width(d, scale)) / 2, (size - 5 * scale) / 2, d, scale, ink);
    }

    /// <summary>Up to two metrics, one number per row.</summary>
    static void Compact(uint[] px, int size, uint ink, IReadOnlyList<TrayItem> items)
    {
        int n = Math.Min(items.Count, 2);
        if (n == 0) { Text(px, size, ink, items); return; }
        int scale = size >= 24 ? 2 : 1, rowH = 5 * scale, gap = Math.Max(1, (size - n * rowH) / (n + 1));
        for (int i = 0; i < n; i++)
        {
            var d = Number(items[i].Text);
            DrawDigits(px, size, (size - Width(d, scale)) / 2, gap + i * (rowH + gap), d, scale, ink);
        }
    }

    /// <summary>One bar per metric (max 4): faint track, filled from the bottom to max(1, h × fraction).</summary>
    static void Bars(uint[] px, int size, uint ink, IReadOnlyList<TrayItem> items)
    {
        int n = Math.Clamp(items.Count, 1, 4), gap = size >= 20 ? 2 : 1;
        int w = Math.Max(2, Math.Min((size - 2 - (n - 1) * gap) / n, 7)), total = n * w + (n - 1) * gap;
        int x0 = (size - total) / 2, top = 1, h = size - 2;
        for (int i = 0; i < n; i++)
        {
            double f = i < items.Count ? Math.Clamp(items[i].Fraction, 0, 1) : 0;
            int fill = Math.Max(1, (int)Math.Round(h * f));
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    Put(px, size, x0 + i * (w + gap) + x, top + y, ink, y >= h - fill ? 1 : 0.28);
        }
    }

    /// <summary>One polyline per metric (max 4), the last 30 fractions across the width, bands stacked.</summary>
    static void Graphs(uint[] px, int size, uint ink, IReadOnlyList<TrayItem> items)
    {
        int n = Math.Clamp(items.Count, 1, 4);
        int band = (size - 2) / n;
        for (int i = 0; i < n && i < items.Count; i++)
        {
            var h = items[i].History;
            int cnt = Math.Min(h.Count, 30), bh = Math.Max(band - (n > 1 ? 1 : 0), 3), y0 = 1 + i * band;
            for (int k = 0; k < size; k++) Put(px, size, k, y0 + bh - 1, ink, 0.18);   // baseline
            if (cnt < 2) continue;
            int prevX = 0, prevY = 0;
            for (int j = 0; j < cnt; j++)
            {
                int x = (int)Math.Round((size - 1.0) * j / (cnt - 1));
                int y = y0 + (int)Math.Round((bh - 1) * (1 - Math.Clamp(h[h.Count - cnt + j], 0, 1)));
                if (j > 0) Line(px, size, prevX, prevY, x, y, ink);
                prevX = x; prevY = y;
            }
        }
    }

    static void Line(uint[] px, int size, int x0, int y0, int x1, int y1, uint ink)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        while (true)
        {
            Put(px, size, x0, y0, ink);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>Gauge ring with a needle; when the status is not green a filled disc with a punched-out "!".</summary>
    static void Gauge(uint[] px, int size, uint ink, bool alert)
    {
        double c = (size - 1) / 2.0, r = size / 2.0 - 1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double d = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                double cover = alert ? Math.Clamp(r + 0.5 - d, 0, 1) : Math.Clamp(1.2 - Math.Abs(d - (r - 0.6)), 0, 1);
                Put(px, size, x, y, ink, cover);
            }
        if (alert)
        {
            int cx = (int)Math.Round(c), top = (int)Math.Round(size * 0.22), bottom = (int)Math.Round(size * 0.58), dot = (int)Math.Round(size * 0.74);
            for (int y = top; y <= bottom; y++) { px[y * size + cx] &= 0x00FFFFFF; }
            px[dot * size + cx] &= 0x00FFFFFF;
        }
        else
            for (int k = 0; k <= (int)(r * 0.7); k++) Put(px, size, (int)Math.Round(c + k * 0.7), (int)Math.Round(c - k * 0.7), ink);
    }
}
