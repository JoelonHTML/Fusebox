using System.Globalization;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Reports;

/// <summary>Lowest / median / average / 95th percentile / highest of the finite values.</summary>
public sealed record Summary(int Count, double Min, double Median, double Avg, double P95, double Max)
{
    public static Summary? Of(IEnumerable<double> values)
    {
        var v = values.Where(double.IsFinite).Order().ToArray();
        return v.Length == 0 ? null : new(v.Length, v[0], Percentile(v, 0.5), v.Average(), Percentile(v, 0.95), v[^1]);
    }

    /// <summary>Linear interpolation between the closest ranks of sorted values.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        double pos = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos), hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}

/// <summary>One tile of the "At a glance" box: label, big value, small unit, a note under it.</summary>
public sealed record Tile(string Label, string Value, string Unit = "", string Note = "");
/// <summary>One line of the "Stands out" box. Level 0 = good / fine, 1 = worth a look, 2 = a problem.</summary>
public sealed record Mark(int Level, string Text, string Detail = "");
/// <summary>A graph line: points in the chart's x unit (NaN y = gap), optional area fill, optional dot + label at the highest point.</summary>
public sealed record Series(string Name, Rgb Color, IReadOnlyList<(double X, double Y)> Points, bool Fill = true, bool Dashed = false, bool MarkMax = false);

/// <summary>
/// The page kit both PDF reports use: a light A4 layout (white page, dark-grey text, one accent, thin light-grey grid lines, 44 pt margins)
/// with a running cursor <see cref="Y"/> and automatic page breaks. Every text passes through the scrubber given to the constructor.
/// </summary>
public sealed class ReportPdf
{
    public static readonly Rgb Ink = Rgb.Gray(0.13), Soft = Rgb.Gray(0.40), Faint = Rgb.Gray(0.58), GridLine = Rgb.Gray(0.88), Card = Rgb.Gray(0.965),
        Accent = new(0.0, 0.42, 0.85), Warn = new(0.80, 0.42, 0.0), Danger = new(0.77, 0.12, 0.12), Good = new(0.05, 0.55, 0.30);
    public const double M = 44, Right = Pdf.Width - M, ContentWidth = Pdf.Width - 2 * M;
    const double Bottom = Pdf.Height - 64;

    readonly Pdf pdf;
    readonly Func<string, string> scrub;
    public double Y;

    public static Rgb LevelColor(int level) => level switch { >= 2 => Danger, 1 => Warn, _ => Good };

    /// <param name="title">"Network report"</param>
    /// <param name="subtitle">date · PC type (no names)</param>
    public ReportPdf(string title, string subtitle, Func<string, string> scrub, bool compress = true)
    {
        pdf = new Pdf(compress);
        this.scrub = scrub;
        Y = M + 4;
        pdf.Text(M, Y + 6, "Fusebox", 10, Accent, bold: true);
        Y += 30;
        pdf.Text(M, Y, S(title), 20, Ink, bold: true);
        Y += 16;
        foreach (var l in Pdf.Wrap(S(subtitle), 9, ContentWidth)) { pdf.Text(M, Y, l, 9, Soft); Y += 12; }
        Y += 4;
        pdf.Line(M, Y, Right, Y, GridLine, 0.75);
        Y += 14;
    }

    string S(string s) => scrub(s);

    /// <summary>Starts a new page when <paramref name="height"/> points no longer fit.</summary>
    public void Need(double height)
    {
        if (Y + height <= Bottom) return;
        pdf.NewPage();
        Y = M;
    }

    public void Gap(double h) => Y += h;

    // ---- text ----

    public void Section(string title)
    {
        Need(130);   // title plus the start of what follows, never a lonely title at the bottom
        Y += 18;
        pdf.Text(M, Y, S(title), 12.5, Ink, bold: true);
        Y += 7;
        pdf.Line(M, Y, Right, Y, GridLine, 0.75);
        Y += 14;
    }

    public void Paragraph(string text, double size = 9.5, Rgb? color = null, double indent = 0)
    {
        foreach (var l in Pdf.Wrap(S(text), size, ContentWidth - indent))
        {
            Need(size + 4);
            pdf.Text(M + indent, Y, l, size, color ?? Ink);
            Y += size + 3.5;
        }
        Y += 2;
    }

    /// <summary>A coloured dot and a wrapped sentence (findings, tips, crashes).</summary>
    public void Bullet(string text, Rgb dot, string detail = "")
    {
        var lines = Pdf.Wrap(S(text), 9.5, ContentWidth - 16);
        Need(lines.Count * 13 + 4);
        pdf.Dot(M + 3.5, Y - 3.2, 2.8, dot);
        foreach (var l in lines) { pdf.Text(M + 14, Y, l, 9.5, Ink); Y += 13; }
        if (detail.Length > 0) foreach (var l in Pdf.Wrap(S(detail), 8.5, ContentWidth - 16)) { Need(12); pdf.Text(M + 14, Y, l, 8.5, Soft); Y += 11.5; }
        Y += 3;
    }

    // ---- the two boxes at the top ----

    /// <summary>Score badge plus headline and detail (Checkup).</summary>
    public void Score(string score, int level, string headline, string detail)
    {
        var lines = Pdf.Wrap(S(headline), 12, ContentWidth - 90, bold: true);
        var dl = Pdf.Wrap(S(detail), 9, ContentWidth - 90);
        double h = Math.Max(56, lines.Count * 15 + dl.Count * 12 + 16);
        Need(h + 12);
        var c = LevelColor(level);
        pdf.Rect(M, Y, 70, h, c.Tint(0.10), radius: 10);
        double w = Pdf.TextWidth(score, 26, true) + Pdf.TextWidth("/10", 10);
        pdf.Text(M + 35 - w / 2, Y + h / 2 + 9, score, 26, c, bold: true);
        pdf.Text(M + 35 - w / 2 + Pdf.TextWidth(score, 26, true), Y + h / 2 + 9, "/10", 10, Soft);
        double y = Y + (h - lines.Count * 15 - dl.Count * 12) / 2 + 11;
        foreach (var l in lines) { pdf.Text(M + 86, y, l, 12, Ink, bold: true); y += 15; }
        foreach (var l in dl) { pdf.Text(M + 86, y, l, 9, Soft); y += 12; }
        Y += h + 12;
    }

    /// <summary>"At a glance": the typical (median) values in a tinted box, three tiles per row.</summary>
    public void Glance(string title, string subtitle, IReadOnlyList<Tile> tiles)
    {
        if (tiles.Count == 0) return;
        int rows = (tiles.Count + 2) / 3;
        double h = 42 + rows * 50;
        Need(h + 10);
        pdf.Rect(M, Y, ContentWidth, h, Accent.Tint(0.06), Accent.Tint(0.25), radius: 10);
        pdf.Text(M + 14, Y + 20, S(title), 11.5, Accent, bold: true);
        pdf.Text(M + 14 + Pdf.TextWidth(S(title), 11.5, true) + 8, Y + 20, Pdf.Fit(S(subtitle), 8.5, ContentWidth - 40 - Pdf.TextWidth(S(title), 11.5, true)), 8.5, Soft);
        double colW = (ContentWidth - 28) / 3;
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            double x = M + 14 + i % 3 * colW, y = Y + 40 + i / 3 * 50;
            pdf.Text(x, y, Pdf.Fit(S(t.Label), 8.5, colW - 8), 8.5, Soft);
            var value = Pdf.Fit(S(t.Value), 17, colW - 30, bold: true);
            pdf.Text(x, y + 20, value, 17, Ink, bold: true);
            if (t.Unit.Length > 0) pdf.Text(x + Pdf.TextWidth(value, 17, true) + 3, y + 20, S(t.Unit), 9, Soft);
            if (t.Note.Length > 0) pdf.Text(x, y + 32, Pdf.Fit(S(t.Note), 7.5, colW - 8), 7.5, Faint);
        }
        Y += h + 10;
    }

    /// <summary>"Stands out": the notable values, most important first, each with a colour mark. Empty list = one green "nothing unusual" line.</summary>
    public void StandsOut(string title, string subtitle, IReadOnlyList<Mark> marks, string nothing)
    {
        var list = marks.Count == 0 ? [new Mark(0, nothing)] : marks;
        var wrapped = list.Select(m => (m, Pdf.Wrap(S(m.Text), 9.5, ContentWidth - 44), m.Detail.Length > 0 ? Pdf.Wrap(S(m.Detail), 8.5, ContentWidth - 44) : [])).ToList();
        double h = 36 + wrapped.Sum(w => w.Item2.Count * 12.5 + w.Item3.Count * 11 + 5) + 6;
        Need(Math.Min(h, Bottom - M) + 10);
        double top = Y;
        if (Y + h > Bottom) h = Bottom - Y;   // a very long list is cut (never more than a page)
        pdf.Rect(M, Y, ContentWidth, h, Card, radius: 10);
        pdf.Text(M + 14, Y + 20, S(title), 11.5, Ink, bold: true);
        pdf.Text(M + 14 + Pdf.TextWidth(S(title), 11.5, true) + 8, Y + 20, Pdf.Fit(S(subtitle), 8.5, ContentWidth - 40 - Pdf.TextWidth(S(title), 11.5, true)), 8.5, Soft);
        double y = Y + 40;
        foreach (var (m, lines, detail) in wrapped)
        {
            if (y + lines.Count * 12.5 > top + h) break;
            pdf.Rect(M + 14, y - 8.5, 4, 10 + (lines.Count - 1) * 12.5, LevelColor(m.Level), radius: 2);
            foreach (var l in lines) { pdf.Text(M + 26, y, l, 9.5, Ink); y += 12.5; }
            foreach (var l in detail) { pdf.Text(M + 26, y, l, 8.5, Soft); y += 11; }
            y += 5;
        }
        Y = top + h + 10;
    }

    // ---- facts and tables ----

    /// <summary>Key/value pairs in two columns; a value too long for half the width gets a whole row.</summary>
    public void Facts(IReadOnlyList<(string Key, string Value)> facts)
    {
        double colW = (ContentWidth - 20) / 2, keyW = 104;
        int col = 0;
        foreach (var (key, value) in facts)
        {
            var v = S(value);
            bool wide = Pdf.TextWidth(v, 9.5) > colW - keyW;
            if (col == 1 && wide) { col = 0; Y += 15; }
            if (col == 0) Need(15);
            double x = M + col * (colW + 20);
            pdf.Text(x, Y, Pdf.Fit(S(key), 8.5, keyW - 6), 8.5, Soft);
            pdf.Text(x + keyW, Y, Pdf.Fit(v, 9.5, (wide ? ContentWidth : colW) - keyW), 9.5, Ink);
            if (wide || col == 1) { col = 0; Y += 15; } else col = 1;
        }
        Y += col == 1 ? 19 : 4;
    }

    /// <summary>A table. <paramref name="widths"/> are fractions of the content width; <paramref name="align"/> has one 'L'/'R' per column
    /// (default: first column left, numbers right).</summary>
    public void Table(string[] head, IReadOnlyList<string[]> rows, double[] widths, string? align = null)
    {
        align ??= "L" + new string('R', widths.Length - 1);
        if (rows.Count == 0) return;
        var xs = new double[widths.Length + 1];
        xs[0] = M;
        for (int i = 0; i < widths.Length; i++) xs[i + 1] = xs[i] + widths[i] * ContentWidth;
        void Row(string[] cells, double size, Rgb color, bool bold)
        {
            for (int c = 0; c < cells.Length && c < widths.Length; c++)
            {
                var t = Pdf.Fit(S(cells[c]), size, xs[c + 1] - xs[c] - 6, bold);
                if (align[c] == 'L') pdf.Text(xs[c] + (c > 0 ? 12 : 0), Y, t, size, color, bold);   // room after a right-aligned number
                else pdf.TextRight(xs[c + 1], Y, t, size, color, bold);
            }
        }
        Need(20 + Math.Min(rows.Count, 3) * 16);
        Row(head, 8, Soft, false);
        Y += 5;
        pdf.Line(M, Y, Right, Y, Rgb.Gray(0.80), 0.6);
        Y += 12;
        foreach (var r in rows)
        {
            Need(16);
            Row(r, 9.5, Ink, false);
            Y += 5;
            pdf.Line(M, Y, Right, Y, GridLine, 0.4);
            Y += 11;
        }
        Y += 4;
    }

    // ---- graphs ----

    /// <summary>"Nice" axis step (1, 2, 2.5 or 5 × 10^k) for about <paramref name="parts"/> parts up to <paramref name="max"/>.</summary>
    public static double NiceStep(double max, int parts = 4)
    {
        if (!(max > 0)) return 1;
        double raw = max / parts, exp = Math.Pow(10, Math.Floor(Math.Log10(raw))), f = raw / exp;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * exp;
    }

    /// <summary>X ticks for a time axis in minutes since <paramref name="from"/>: every 3 hours ("15:00") up to 1.5 days, else every midnight ("Tue 17").</summary>
    public static List<(double X, string Label)> TimeTicks(DateTime from, DateTime to)
    {
        var list = new List<(double, string)>();
        var c = Culture;
        if ((to - from).TotalHours <= 36)
        {
            for (var t = from.Date.AddHours(from.Hour / 3 * 3); t <= to; t = t.AddHours(3))
                if (t >= from) list.Add(((t - from).TotalMinutes, t.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }
        else
            for (var t = from.Date; t <= to; t = t.AddDays(1))
                if (t >= from) list.Add(((t - from).TotalMinutes, t.ToString("ddd d", c)));
        return list;
    }

    /// <summary>Many points → at most <paramref name="buckets"/> means along x; an empty bucket becomes a gap (PC off or asleep).</summary>
    public static List<(double X, double Y)> Bucket(IReadOnlyList<(double X, double Y)> pts, double x0, double x1, int buckets = 360)
    {
        if (pts.Count <= buckets || x1 <= x0) return pts.ToList();
        var sum = new double[buckets]; var n = new int[buckets];
        foreach (var (x, y) in pts)
        {
            if (!double.IsFinite(y)) continue;
            int b = (int)Math.Clamp((x - x0) / (x1 - x0) * buckets, 0, buckets - 1);
            sum[b] += y; n[b]++;
        }
        var list = new List<(double, double)>(buckets);
        for (int b = 0; b < buckets; b++) list.Add((x0 + (b + 0.5) * (x1 - x0) / buckets, n[b] > 0 ? sum[b] / n[b] : double.NaN));
        return list;
    }

    /// <summary>
    /// Line/area graph with light grid lines, y labels (via <paramref name="yLabel"/>), x tick labels and a legend.
    /// <paramref name="yMax"/> null = rounded up from the data. Points with NaN y break the line.
    /// </summary>
    public void Chart(string title, double x0, double x1, IReadOnlyList<(double X, string Label)> ticks, Func<double, string> yLabel,
        IReadOnlyList<Series> series, double? yMax = null, double height = 110, string caption = "")
    {
        var all = series.SelectMany(s => s.Points).Select(p => p.Y).Where(double.IsFinite).ToList();
        if (all.Count == 0) return;
        Need(height + 46);
        ChartHead(title, series.Select(s => (s.Name, s.Color, s.Dashed)).ToList());
        double step = NiceStep(yMax ?? all.Max()), top = yMax ?? Math.Max(step, Math.Ceiling(all.Max() / step - 1e-9) * step);
        if (yMax != null) step = NiceStep(top);
        double px0 = M + 38, px1 = Right - 4, py0 = Y, py1 = Y + height;
        double X(double x) => px0 + (x - x0) / Math.Max(x1 - x0, 1e-9) * (px1 - px0);
        double Yv(double y) => py1 - Math.Clamp(y / top, 0, 1.04) * height;
        Grid(px0, px1, py0, py1, top, step, yLabel);
        foreach (var (x, l) in ticks)
            if (x >= x0 && x <= x1) pdf.TextCenter(X(x), py1 + 11, S(l), 7.5, Faint);

        foreach (var s in series)
        {
            var pts = Bucket(s.Points, x0, x1);
            var seg = new List<(double X, double Y)>();
            void Flush()
            {
                if (seg.Count == 1) pdf.Dot(seg[0].X, seg[0].Y, 1.2, s.Color);
                else if (seg.Count > 1)
                {
                    if (s.Fill) pdf.Polygon([(seg[0].X, py1), .. seg, (seg[^1].X, py1)], s.Color.Tint(0.14));
                    pdf.Polyline(seg, s.Color, 1.1, s.Dashed);
                }
                seg.Clear();
            }
            foreach (var (x, y) in pts)
            {
                if (!double.IsFinite(y)) { Flush(); continue; }
                seg.Add((X(x), Yv(y)));
            }
            Flush();
            var finite = pts.Where(p => double.IsFinite(p.Y)).ToList();   // the drawn (bucketed) line, so the dot sits on it
            if (s.MarkMax && finite.Count > 0)
            {
                var (mx, my) = finite.MaxBy(p => p.Y);
                double dx = X(mx), dy = Yv(my);
                pdf.Dot(dx, dy, 2.6, Rgb.Gray(1));
                pdf.Dot(dx, dy, 1.9, s.Color);
                var t = yLabel(my);
                double tw = Pdf.TextWidth(t, 7.5, true);
                pdf.Text(Math.Clamp(dx - tw / 2, px0, px1 - tw), Math.Max(dy - 5, py0 + 2), t, 7.5, Ink, bold: true);
            }
        }
        Y = py1 + 24;
        if (caption.Length > 0) { pdf.Text(px0, Y, S(caption), 7.5, Faint); Y += 12; }
        Y += 12;
    }

    void ChartHead(string title, List<(string Name, Rgb Color, bool Dashed)> legend)
    {
        pdf.Text(M, Y, S(title), 10, Ink, bold: true);
        double x = Right;
        foreach (var (name, color, dashed) in Enumerable.Reverse(legend).Where(l => l.Name.Length > 0))
        {
            var n = S(name);
            x -= Pdf.TextWidth(n, 8);
            pdf.Text(x, Y, n, 8, Soft);
            x -= 18;
            pdf.Line(x, Y - 3, x + 13, Y - 3, color, 1.6, dashed);
            x -= 12;
        }
        Y += 12;
    }

    void Grid(double px0, double px1, double py0, double py1, double top, double step, Func<double, string> yLabel)
    {
        for (double v = 0; v <= top + step / 1000; v += step)
        {
            double y = py1 - v / top * (py1 - py0);
            pdf.Line(px0, y, px1, y, v == 0 ? Rgb.Gray(0.78) : GridLine, v == 0 ? 0.6 : 0.4);
            pdf.TextRight(px0 - 5, y + 2.5, S(yLabel(v)), 7.5, Faint);
        }
    }

    /// <summary>Vertical bars, one group per label (one bar per series in a group); <paramref name="highlight"/> = group drawn in the accent.</summary>
    public void Bars(string title, IReadOnlyList<string> labels, IReadOnlyList<(string Name, Rgb Color, double[] Values)> series, Func<double, string> yLabel,
        int labelEvery = 1, int? highlight = null, double height = 90, bool valueOnTop = false, string caption = "")
    {
        int n = labels.Count;
        var all = series.SelectMany(s => s.Values).Where(double.IsFinite).ToList();
        if (n == 0 || all.Count == 0) return;
        Need(height + 46);
        ChartHead(title, series.Count > 1 ? series.Select(s => (s.Name, s.Color, false)).ToList() : []);
        double step = NiceStep(all.Max()), top = Math.Max(step, Math.Ceiling(all.Max() / step - 1e-9) * step);
        double px0 = M + 38, px1 = Right - 4, py0 = Y + (valueOnTop ? 8 : 0), py1 = Y + height;
        Grid(px0, px1, py0, py1, top, step, yLabel);
        double slot = (px1 - px0) / n, barW = Math.Min(28, slot * 0.7 / series.Count);
        for (int i = 0; i < n; i++)
        {
            double gx = px0 + i * slot + (slot - barW * series.Count) / 2;
            for (int k = 0; k < series.Count; k++)
            {
                double v = series[k].Values[i];
                if (!double.IsFinite(v) || v <= 0) continue;
                double h = v / top * (py1 - py0);
                var color = highlight == i && k == 0 ? Accent : series[k].Color;
                pdf.Rect(gx + k * barW + 0.5, py1 - h, barW - 1, h, color, radius: Math.Min(2, barW / 3));
                if (valueOnTop) pdf.TextCenter(gx + (k + 0.5) * barW, py1 - h - 3, S(yLabel(v)), 6.5, Soft);
            }
            if (i % labelEvery == 0) pdf.TextCenter(px0 + (i + 0.5) * slot, py1 + 11, S(labels[i]), 7.5, Faint);
        }
        Y = py1 + 24;
        if (caption.Length > 0) { pdf.Text(px0, Y, S(caption), 7.5, Faint); Y += 12; }
        Y += 12;
    }

    /// <summary>Horizontal bars: label, bar scaled to the largest value, value text on the right (top apps).</summary>
    public void HBars(string title, IReadOnlyList<(string Label, double Value, string Text)> rows, Rgb color)
    {
        if (rows.Count == 0) return;
        Need(22 + Math.Min(rows.Count, 3) * 16);
        pdf.Text(M, Y, S(title), 10, Ink, bold: true);
        Y += 14;
        double max = Math.Max(rows.Max(r => r.Value), 1e-9), lx = M + 150, rx = Right - 64;
        foreach (var (label, value, text) in rows)
        {
            Need(16);
            pdf.Text(M, Y, Pdf.Fit(S(label), 9, 144), 9, Ink);
            pdf.Rect(lx, Y - 7, rx - lx, 7, Rgb.Gray(0.94), radius: 3.5);
            pdf.Rect(lx, Y - 7, Math.Max(3.5, (rx - lx) * Math.Clamp(value / max, 0, 1)), 7, color, radius: 3.5);
            pdf.TextRight(Right, Y, S(text), 9, Ink, bold: true);
            Y += 16;
        }
        Y += 6;
    }

    /// <summary>A rail across the period with coloured spans (busy moments) and dots (peaks).</summary>
    public void Timeline(string title, double x0, double x1, IReadOnlyList<(double X, string Label)> ticks,
        IReadOnlyList<(double From, double To, Rgb Color)> spans, IReadOnlyList<(double X, Rgb Color)> dots, IReadOnlyList<(string Name, Rgb Color)> legend)
    {
        Need(54);
        ChartHead(title, legend.Select(l => (l.Name, l.Color, false)).ToList());
        double px0 = M + 38, px1 = Right - 4, y = Y + 6;
        double X(double x) => px0 + Math.Clamp((x - x0) / Math.Max(x1 - x0, 1e-9), 0, 1) * (px1 - px0);
        pdf.Rect(px0, y, px1 - px0, 10, Rgb.Gray(0.95), radius: 5);
        foreach (var (a, b, c) in spans) pdf.Rect(X(a), y, Math.Max(2.5, X(b) - X(a)), 10, c, radius: 1.5);
        foreach (var (x, c) in dots) { pdf.Dot(X(x), y + 5, 3.2, Rgb.Gray(1)); pdf.Dot(X(x), y + 5, 2.4, c); }
        foreach (var (x, l) in ticks)
            if (x >= x0 && x <= x1) { pdf.Line(X(x), y + 11, X(x), y + 14, Rgb.Gray(0.8), 0.4); pdf.TextCenter(X(x), y + 23, S(l), 7.5, Faint); }
        Y = y + 36;
    }

    // ---- finish ----

    /// <summary>Footer on every page ("Fusebox · Network report · date" left, "Page 1 of 2" right) and the privacy note at the bottom of page 1.</summary>
    public byte[] Save(string footer, string note, string title)
    {
        int n = pdf.PageCount;
        for (int i = 0; i < n; i++)
        {
            pdf.Select(i);
            double fy = Pdf.Height - 30;
            if (i == 0 && note.Length > 0)
            {
                pdf.Dot(M + 3, fy - 19.5, 2.2, Good);
                pdf.Text(M + 10, fy - 17, S(note), 8, Soft);
            }
            pdf.Line(M, fy - 10, Right, fy - 10, GridLine, 0.5);
            pdf.Text(M, fy, S(footer), 7.5, Faint);
            pdf.TextRight(Right, fy, L("Page %lld of %lld", i + 1, n), 7.5, Faint);
        }
        return pdf.Save(S(title));
    }
}
