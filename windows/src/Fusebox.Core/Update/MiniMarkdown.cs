using System.Text.RegularExpressions;

namespace Fusebox.Core.Update;

/// <summary>A piece of a line: plain or bold text, or a link (https only).</summary>
public sealed record MdSpan(string Text, bool Bold = false, string? Url = null);

/// <summary>A line of release notes: a bullet ("- " / "* "), a heading ("#") or plain text.</summary>
public sealed record MdLine(bool Bullet, bool Heading, IReadOnlyList<MdSpan> Spans);

/// <summary>
/// Just enough Markdown for GitHub release notes (§9.16 "What's new"): bullets, headings, **bold**, [links](https://…), `code`.
/// No HTML, no images; links to anything but https are shown as plain text, so release notes can't make a button that opens a program.
/// </summary>
public static partial class MiniMarkdown
{
    [GeneratedRegex(@"\*\*(?<b>[^*]+)\*\*|\[(?<t>[^\]]+)\]\((?<u>[^)\s]+)\)|`(?<c>[^`]+)`")]
    private static partial Regex Inline();

    public static List<MdLine> Parse(string md)
    {
        var lines = new List<MdLine>();
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            bool bullet = false, heading = false;
            var t = line.TrimStart();
            if (t.StartsWith("- ") || t.StartsWith("* ")) { bullet = true; t = t[2..]; }
            else if (t.StartsWith('#')) { heading = true; t = t.TrimStart('#').TrimStart(); }
            lines.Add(new(bullet, heading, Spans(t, heading)));
        }
        return lines;
    }

    static List<MdSpan> Spans(string s, bool allBold)
    {
        var spans = new List<MdSpan>();
        int at = 0;
        foreach (Match m in Inline().Matches(s))
        {
            if (m.Index > at) spans.Add(new(s[at..m.Index], allBold));
            if (m.Groups["b"].Success) spans.Add(new(m.Groups["b"].Value, true));
            else if (m.Groups["c"].Success) spans.Add(new(m.Groups["c"].Value, allBold));
            else
            {
                string url = m.Groups["u"].Value;
                bool ok = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
                spans.Add(ok ? new(m.Groups["t"].Value, allBold, url) : new(m.Groups["t"].Value, allBold));
            }
            at = m.Index + m.Length;
        }
        if (at < s.Length) spans.Add(new(s[at..], allBold));
        return spans;
    }
}
