using System.Text;
using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Core.Network;
using Fusebox.Core.Processes;
using Fusebox.Core.Reports;

namespace Fusebox.Tests;

public class PdfTests
{
    static readonly ScrubIdentity Id = new("jdoe1", "Jane Doe", "JANES-LAPTOP", "CONTOSO");
    static readonly DateTime Now = new(2026, 11, 21, 22, 40, 0);

    /// <summary>Checks the file the way a reader does: header, startxref → xref table, every offset lands on "n 0 obj", trailer /Size, %%EOF.</summary>
    static void AssertWellFormed(byte[] pdf)
    {
        var s = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4\n", s);
        Assert.EndsWith("%%EOF\n", s);
        int sx = s.LastIndexOf("startxref\n", StringComparison.Ordinal);
        long xref = long.Parse(s[(sx + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref\n0 ", s[(int)xref..]);
        var lines = s[(int)xref..].Split('\n');
        int count = int.Parse(lines[1].Split(' ')[1]);
        Assert.Equal("0000000000 65535 f ", lines[2]);
        for (int i = 1; i < count; i++)
        {
            var e = lines[2 + i];
            Assert.Equal(19, e.Length);   // 20 bytes with the newline
            Assert.EndsWith(" 00000 n ", e);
            int off = int.Parse(e[..10]);
            Assert.StartsWith($"{i} 0 obj\n", s[off..]);
        }
        Assert.Contains($"/Size {count} ", s);
        Assert.Equal(count - 1, System.Text.RegularExpressions.Regex.Matches(s, @"(?m)^\d+ 0 obj$").Count);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, @"/Length (\d+)[^>]*>>\nstream\n"))
        {
            int start = m.Index + m.Length, len = int.Parse(m.Groups[1].Value);
            Assert.Equal("\nendstream", s.Substring(start + len, 10));
        }
    }

    [Fact]
    public void Writer_makes_a_well_formed_file_with_pages()
    {
        var p = new Pdf(compress: false);
        p.Text(40, 60, "Hello (world) \\ back", 12, Rgb.Gray(0.1), bold: true);
        p.Rect(40, 80, 100, 40, new Rgb(0, 0.4, 0.8), Rgb.Gray(0.5), radius: 6);
        p.Polyline([(40, 200), (80, 180), (120, 220)], Rgb.Gray(0.2));
        p.Polygon([(40, 300), (80, 280), (120, 300)], Rgb.Gray(0.9));
        p.NewPage();
        p.Line(40, 40, 200, 40, Rgb.Gray(0.8), dashed: true);
        var bytes = p.Save("Test");
        AssertWellFormed(bytes);
        var s = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Count 2", s);
        Assert.Contains(@"(Hello \(world\) \\ back) Tj", s);
        Assert.Contains("/BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding", s);
        Assert.Contains(" c ", s);   // Bézier corners
        AssertWellFormed(new Pdf().Save());   // compressed, one empty page
    }

    [Fact]
    public void Accents_go_to_cp1252_and_other_symbols_get_a_stand_in()
    {
        Assert.Equal(new byte[] { 0xE9, 0xEB, 0xFC, 0xDF, 0xE7, 0xF1, 0xC9, 0x80, 0x96, 0x85, 0xB7, 0xB0 }, Pdf.Encode("éëüßçñÉ€–…·°"));
        Assert.Equal(Encoding.ASCII.GetBytes(">= -> - ?"), Pdf.Encode("≥ → − 🙂"));
        Assert.Equal(Encoding.ASCII.GetBytes("ab"), Pdf.Encode("a\u0001b"));
        var p = new Pdf(compress: false);
        p.Text(0, 10, "Café", 10, Rgb.Gray(0));
        Assert.Contains(@"(Caf\351) Tj", Encoding.ASCII.GetString(p.Save()));
    }

    [Fact]
    public void Text_width_and_wrap_use_the_font_metrics()
    {
        Assert.Equal(5.56, Pdf.TextWidth("a", 10), 3);          // Helvetica 'a' = 556
        Assert.Equal(6.11, Pdf.TextWidth("b", 10, true), 3);    // Helvetica-Bold 'b' = 611
        var lines = Pdf.Wrap("one two three four five six seven", 10, 60);
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(Pdf.TextWidth(l, 10) <= 60));
        Assert.Equal("one two three four five six seven", string.Join(" ", lines));
        Assert.EndsWith("…", Pdf.Fit("Averyveryverylongappname.exe", 10, 50));
    }

    [Fact]
    public void Summary_and_axis_steps()
    {
        var s = Summary.Of([5, 1, 3, double.NaN, 2, 4])!;
        Assert.Equal((5, 1, 3, 3, 5), (s.Count, s.Min, s.Median, s.Avg, s.Max));
        Assert.Equal(4.8, s.P95, 6);
        Assert.Null(Summary.Of([double.NaN]));
        Assert.Equal(25, ReportPdf.NiceStep(100));
        Assert.Equal(2.5, ReportPdf.NiceStep(9));
        Assert.Equal(0.5, ReportPdf.NiceStep(1.7));
        var b = ReportPdf.Bucket(Enumerable.Range(0, 1000).Select(i => ((double)i, i < 500 ? 1.0 : double.NaN)).ToList(), 0, 1000, 100);
        Assert.Equal(100, b.Count);
        Assert.Equal(1, b[10].Y);
        Assert.True(double.IsNaN(b[80].Y));   // nothing measured there = a gap
        Assert.Equal(["00:00", "03:00", "06:00"], ReportPdf.TimeTicks(new DateTime(2026, 1, 1), new DateTime(2026, 1, 1, 7, 0, 0)).Select(t => t.Label));
    }

    // ---- sample reports (written next to the test binary so they can be opened) ----

    static NetReportInput NetSample(string standard = "Wi-Fi 6 (802.11ax)")
    {
        var rnd = new Random(4);
        var router = Enumerable.Range(0, 60).Select(i => 2 + rnd.NextDouble() * 2 + (i == 41 ? 30 : 0)).ToList();
        var inet = Enumerable.Range(0, 60).Select(i => 14 + rnd.NextDouble() * 6 + (i is > 40 and < 44 ? 90 : 0)).ToList();
        var speeds = Enumerable.Range(0, 8).Select(i => new SpeedEntry(Now.ToUniversalTime().AddDays(-i * 2), i == 0 ? 41 : 90 + rnd.Next(-15, 15), 18 + rnd.Next(-3, 3), 13,
            i % 2 == 0 ? SpeedKind.Full : SpeedKind.Quick, 110, 22, i == 0 ? 95 : 30, i == 0 ? 0.3 : 0.05)).ToList();
        return new NetReportInput(Now, "Wi-Fi", Vpn: false, ProfileCategory: 1, EncryptedDns: true, Metered: false,
            Rssi: -72, LinkMbps: 433, Channel: 36, Band: "5 GHz", WidthMhz: 80, Standard: standard,
            Router: PingStats.From(router.Take(10).ToList(), 10), Internet: PingStats.From(inet.Take(9).ToList(), 10), DnsMs: 140, Ipv6Internet: true,
            Finding: new Finding(FindCode.WeakButWorks, "The Wi-Fi signal is weak, but the internet works.", null, "router 3 ms · internet 16 ms · DNS 140 ms"),
            Speed: speeds[0], Devices: 14, Speeds: speeds, LiveRouter: router, LiveInternet: inet, Pc: "Intel Core i7-1165G7 · 16 GB · Windows 11 Pro 24H2");
    }

    static CheckupPdfInput CheckupSample(bool week = false)
    {
        var rnd = new Random(7);
        var minutes = new List<CsvLog.Minute>();
        var apps = new List<AppsLog.Row>();
        var days = new List<DayData>();
        foreach (var day in Enumerable.Range(0, week ? 7 : 1).Select(d => Now.Date.AddDays(-d)).Reverse())
        {
            int first = minutes.Count, firstApp = apps.Count;
            var end = day.AddHours(day == Now.Date ? 22.5 : 18 + rnd.Next(0, 5));
            for (var t = day.AddHours(8); t < end; t = t.AddMinutes(1))
            {
                if (t.Hour == 13 && t.Minute < 40) continue;   // lunch: PC asleep
                bool busy = t.Hour == 15 && t.Minute is >= 10 and < 32;
                double cpu = busy ? 88 + rnd.NextDouble() * 10 : 6 + rnd.NextDouble() * 14 + (t.Hour == 10 ? 25 : 0);
                double ram = 7.5 + (t - day).TotalHours * 0.18 + rnd.NextDouble() * 0.4;
                minutes.Add(new(t, cpu, ram, ram > 11.2 ? 2 : 1, double.NaN, 61 - (t - day).TotalHours * 0.3, 476, 18 + rnd.NextDouble() * 8 + (t.Hour == 20 ? 220 : 0),
                    false, t == day.AddHours(19.5), -58 - rnd.Next(0, 6) - (t.Hour == 21 ? 20 : 0), 30, (t.Hour is >= 19 and < 21 ? 2.4e6 : 40e3) * (0.5 + rnd.NextDouble()), 25e3 * (0.5 + rnd.NextDouble())));
                apps.Add(new(t, "Microsoft Edge", "app", busy ? 30 : cpu * 0.4, 1.6 + rnd.NextDouble() * 0.2, 0, 0, 0));
                apps.Add(new(t, "Microsoft Teams", "app", cpu * 0.25, 1.1, 0, 0, 0));
                apps.Add(new(t, "Visual Studio Code", "app", busy ? 50 : cpu * 0.15, 0.9, 0, 0, 0));
                apps.Add(new(t, "Windows Explorer", "app", 1, 0.2, 0, 0, 0));
            }
            days.Add(new DayData(day, minutes[first..], apps[firstApp..], [], []));
        }
        var day0 = Now.Date;
        var peaks = new List<Peak>
        {
            new() { Kind = PeakKind.Cpu, Start = day0.AddHours(15).AddMinutes(10), End = day0.AddHours(15).AddMinutes(31), At = day0.AddHours(15).AddMinutes(22), Top = 99, App = "Visual Studio Code" },
            new() { Kind = PeakKind.Cpu, Start = day0.AddHours(10.5), End = day0.AddHours(10.5).AddSeconds(40), At = day0.AddHours(10.5), Top = 86, App = "Microsoft Edge" },
            new() { Kind = PeakKind.Mem, Start = day0.AddHours(21), End = day0.AddHours(21).AddMinutes(3), At = day0.AddHours(21), Top = 2, App = "Microsoft Edge" },
        };
        var crashes = new CrashSummary([new AppCrash("Spotify", 2, 1, 0), new AppCrash("Game Bar", 0, 1, 0)], 0, 0, 1);
        days[^1] = days[^1] with { Peaks = peaks };
        var report = Analysis.Build(week ? Period.Week : Period.Today, Now, days, [new DayData(day0.AddDays(-7), minutes.Take(300).ToList(), [], [], [])], 8, crashes);
        var tips = Tips.Build(report, new TipContext { UptimeDays = 16, RestartDays = 14, Battery = new BatteryHealth(78, 412) });
        var info = new ReportInfo
        {
            Processor = "Intel Core i7-1165G7", MemoryGb = 16, Windows = "Windows 11 Pro 24H2 (26100.2314)", UptimeMinutes = 16 * 1440 + 200,
            DiskTotalGb = 476, DiskFreeGb = 54, StabilityIndex = 7.4,
        };
        return new CheckupPdfInput(report, tips, info, minutes, Now, StartupApps: 11, BatteryPct: 78, BatteryCycles: 412);
    }

    static void Write(string name, byte[] pdf) => File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, name), pdf);

    [Fact]
    public void Sample_reports_render_and_are_well_formed()
    {
        var net = NetPdf.Build(NetSample(), Id);
        AssertWellFormed(net);
        Write("sample-network.pdf", net);
        Write("sample-network-empty.pdf", NetPdf.Build(new NetReportInput(Now, null), Id));

        var checkup = CheckupPdf.Build(CheckupSample(), Id);
        AssertWellFormed(checkup);
        Write("sample-checkup.pdf", checkup);
        Write("sample-checkup-week.pdf", CheckupPdf.Build(CheckupSample(week: true), Id));
        try
        {
            Strings.Load("nl");
            var nl = CheckupPdf.Build(CheckupSample(), Id);
            AssertWellFormed(nl);
            Write("sample-checkup-nl.pdf", nl);
            Write("sample-network-nl.pdf", NetPdf.Build(NetSample(), Id));
        }
        finally { Strings.Load("en"); }
    }

    [Fact]
    public void Typical_values_and_what_stands_out()
    {
        var x = CheckupSample();
        var marks = CheckupPdf.Marks(x, x.Minutes);
        Assert.Contains(marks, m => m.Text.StartsWith("Highest CPU: 99%") && m.Text.Contains("Visual Studio Code"));
        Assert.Contains(marks, m => m.Text == "Unexpected shutdowns: 1" && m.Level == 2);
        Assert.Contains(marks, m => m.Text.StartsWith("Battery worn"));
        Assert.Equal(marks.OrderByDescending(m => m.Level).Select(m => m.Text), marks.Select(m => m.Text));   // most important first
        var s = Encoding.Latin1.GetString(CheckupPdf.Build(x, Id, compress: false));
        foreach (var t in new[] { "At a glance", "Stands out", "Statistics", "Processor \\(CPU\\)", "CPU by hour of the day", "Top apps", "Visual Studio Code",
                     "Crashes and reports from Windows", "Spotify", "Page 1 of ", "Contains no Wi-Fi name" })
            Assert.Contains(t, s);
    }

    [Fact]
    public void Network_pdf_has_its_sections_and_scrubs_everything()
    {
        var s = Encoding.Latin1.GetString(NetPdf.Build(NetSample(), Id, compress: false));
        foreach (var t in new[] { "Network report", "Router ping", "Internet ping", "Slow DNS lookups \\(140 ms\\)", "Weak Wi-Fi signal \\(-72 dBm\\)",
                     "bufferbloat", "Reply times, last 60 seconds", "Last 8 speed tests", "Find the problem", "Devices found" })
            Assert.Contains(t, s);
        var hostile = NetSample("10.0.0.7 CONTOSO") with { Finding = new Finding(FindCode.AllGood, "ok on JANES-LAPTOP via 192.168.1.1 aa:bb:cc:dd:ee:ff", true, "user jdoe1"), Pc = "C:\\Users\\jdoe1 Jane Doe" };
        var h = Encoding.Latin1.GetString(NetPdf.Build(hostile, Id, compress: false));
        foreach (var bad in new[] { "192.168", "10.0.0.7", "aa:bb", "JANES", "jdoe1", "CONTOSO", "Jane" }) Assert.DoesNotContain(bad, h);
    }
}
