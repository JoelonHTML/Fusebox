using System.Diagnostics;
using Fusebox.App.Controls;
using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Core.Processes;
using Fusebox.Platform.Checkup;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>What a card needs from the page. Cards load their own data in the background and only touch the UI while <see cref="Alive"/>.</summary>
internal sealed class CardCtx
{
    public Report R = new();
    public IReadOnlyList<CheckupTip> Tips = [];
    public XamlRoot? Root;
    public Func<bool> Alive = () => true;
    public double Width = 600;
    public Action Summaries = () => { };
    public DispatcherQueue Q = DispatcherQueue.GetForCurrentThread();
    public HashSet<string> Hide = [];
    public bool Show(string id) => !Hide.Contains(id);
    public bool HasBattery;
}

internal static class Shell
{
    public static void Launch(string uri) { try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(uri)); } catch (Exception e) when (e is UriFormatException or InvalidOperationException) { } }

    /// <summary>Opens Explorer with the file selected ("Show in Explorer").</summary>
    public static void Reveal(string path)
    {
        if (path.Contains('"')) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = false }); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public static void OpenFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Cards of the Performance, Stability and History groups (§5.6).</summary>
internal static class CheckupCards
{
    // ---- tips (§5.6.1) ----

    /// <summary>The group's tips as soft tinted blocks (like a Fluent InfoBar without close button): icon, bold head, text, one action link.
    /// <paramref name="skipUri"/> = an action the group already offers itself (no second link to the same place).</summary>
    public static UIElement? TipsList(CardCtx c, string group, string? skipUri = null)
    {
        if (!c.Show("tips")) return null;
        var mine = c.Tips.Where(t => t.Group == group).ToList();
        if (mine.Count == 0) return null;
        var s = new StackPanel { Spacing = 8 };
        foreach (var t in mine)
        {
            var (h, rest) = t.Split();
            var tone = Ui.Tone(t.Serious ? 1 : 0);
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var icon = Ui.Icon(t.Serious ? "\uE7BA" : "\uE946", 14, tone);
            icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 2, 0, 0);
            g.Children.Add(icon);
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(Ui.Label(h, 13, bold: true));
            if (rest.Length > 0) text.Children.Add(Ui.Label(rest, 12, secondary: true));
            if (t.Action is { } a && (a.StopApp != null || a.Uri != skipUri))
            {
                var link = a.StopApp != null
                    ? Ui.Link(L("Stop %@", a.StopApp), () => StopApp(c, a.StopApp), danger: true)
                    : Ui.Link(a.Label, () => Shell.Launch(a.Uri!));
                link.Margin = new Thickness(0, 2, 0, 0);
                text.Children.Add(link);
            }
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            s.Children.Add(new Border { Child = g, Background = Ui.Tint(tone, 0.12), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10) });
        }
        return s;
    }

    public static async void StopApp(CardCtx c, string name)
    {
        var g = App.Live.Apps?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.CanStop);
        if (g == null || c.Root == null) { await new ContentDialog { XamlRoot = c.Root, Title = L("%@ is no longer running", name), CloseButtonText = L("OK") }.ShowAsync(); return; }
        await StopFlow.RunAsync(c.Root, g);
        WhyState.Text = null;
    }

    // ---- "Why is my PC slow?" (§5.6.2) ----

    public static class WhyState
    {
        public static string? Text, Culprit;
        public static bool NoApps, Checking;
    }

    public static UIElement Why(CardCtx c)
    {
        var body = new StackPanel { Spacing = 6 };
        void Fill()
        {
            body.Children.Clear();
            var check = Ui.Primary(WhyState.Text == null ? L("Check now") : L("Check again"), Run);
            check.IsEnabled = !WhyState.Checking;
            body.Children.Add(Ui.CardTitle("", L("Why is my PC slow?"), 0, check));
            if (WhyState.Checking) body.Children.Add(Ui.Spinner());
            if (WhyState.Text != null)
            {
                body.Children.Add(Ui.Label(WhyState.Text, 12));
                if (WhyState.NoApps) body.Children.Add(Ui.Small(L("Apps are measured every minute — the first measurement is coming soon.")));
                if (WhyState.Culprit is { } cul && (App.Live.Apps?.Any(g => g.Name == cul && g.CanStop) ?? false))
                    body.Children.Add(Ui.Link(L("Stop %@", cul), () => { StopApp(c, cul); Fill(); }, danger: true));
            }
        }
        async void Run()
        {
            WhyState.Checking = true; Fill();
            var live = App.Live;
            var last = live.Last;
            double cpu = double.IsFinite(live.Cpu30) ? live.Cpu30 : last?.Cpu ?? 0;
            var input = new DiagInput
            {
                Cpu = cpu, Pressure = last?.Pressure ?? 1, TempC = last?.TempC ?? double.NaN, DiskFreeGb = (last?.DiskFree ?? double.NaN) / 1e9, DiskTotalGb = (last?.DiskTotal ?? double.NaN) / 1e9,
                PingMs = last?.PingMs ?? double.NaN, UptimeDays = Environment.TickCount64 / 86_400_000.0, RestartDays = Prefs.Int("restartDays"), Report = c.R, Now = DateTime.Now,
                Apps = (live.Apps ?? []).Select(g => new DiagApp(g.Name, g.Kind == AppKind.Windows, g.Cpu, g.Ram / 1073741824.0)).ToList(),
            };
            var cores = Environment.ProcessorCount;
            input.JobsNow = await Task.Run(() => { try { return WindowsJobs.Busy(cores); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return []; } });
            var d = Diagnose.Run(input);
            WhyState.Text = d.Text; WhyState.Culprit = d.Culprit; WhyState.NoApps = d.NoAppData; WhyState.Checking = false;
            if (c.Alive()) Fill();
        }
        Fill();
        return body;
    }

    // ---- Top 5 (§5.6.3) ----

    public static UIElement Top(CardCtx c, bool cpu)
    {
        var list = cpu ? c.R.TopCpu : c.R.TopRam;
        var s = new StackPanel { Spacing = 5 };
        s.Children.Add(Ui.CardTitle(cpu ? "" : "", cpu ? L("Top 5 CPU") : L("Top 5 RAM")));
        if (list.Count == 0) s.Children.Add(Ui.Small(L("No data yet")));
        double top = list.Count > 0 ? list.Max(a => cpu ? a.Cpu : a.MemGb) : 1;
        foreach (var a in list)
        {
            double v = cpu ? a.Cpu : a.MemGb;
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(Ui.ItemRow(a.Name, cpu ? Fmt.Pct(v) : Fmt.One(v) + " GB", 12, false));
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            bar.Children.Add(Ui.Bar(top > 0 ? v / top : 0, 70, 0.03));
            bar.Children.Add(Ui.Label(CheckupFmt.Duration(a.Minutes), 10, secondary: true, wrap: false));
            row.Children.Add(bar);
            s.Children.Add(row);
        }
        s.MinWidth = 190;
        return s;
    }

    // ---- Peaks (§5.6.4) ----

    public static UIElement Peaks(CardCtx c)
    {
        var r = c.R;
        var s = new StackPanel { Spacing = 5 };
        s.Children.Add(Ui.CardTitle("", L("Peaks")));
        if (r.Peaks.Count == 0)
            s.Children.Add(Ui.Calm(L("No peaks recorded %@ (CPU or GPU ≥ 80%%, hot, memory pressure or heavy disk use)", r.When)));
        else
        {
            s.Children.Add(Ui.Headline(r.Peaks.Count.ToString(), r.Peaks.Count == 1 ? L("peak %@", r.When) : L("peaks %@", r.When), 0));
            var (from, _) = Periods.Range(r.Period, r.Made);
            int days = Periods.Days(r.Period).Length;
            var tl = new PeakTimeline(r.Peaks, from, days);
            tl.Margin = new Thickness(0, 2, 0, 2);
            s.Children.Add(tl);
            var axis = new Grid();
            string[] labels = days == 1 ? ["0:00", "12:00", "24:00"] : [from.ToString("ddd", Culture), L("today")];
            for (int i = 0; i < labels.Length; i++)
            {
                axis.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var l = Ui.Num(labels[i], 9, secondary: true);
                l.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == labels.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                Grid.SetColumn(l, i);
                axis.Children.Add(l);
            }
            s.Children.Add(axis);
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            foreach (var k in r.Peaks.Select(p => p.Kind).Distinct().OrderBy(k => k))
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                item.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = PeakTimeline.KindBrush(k), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(Ui.Label(Peak.Title(k), 10, secondary: true, wrap: false));
                legend.Children.Add(item);
            }
            s.Children.Add(new ScrollViewer { Content = legend, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Disabled });
            foreach (var g in r.Peaks.GroupBy(p => p.Kind)) s.Children.Add(Ui.Label(PeakSentence(g.ToList(), r.When), 11));
            var rows = r.Peaks.OrderByDescending(p => p.Start).Select(p =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(Ui.Num(CheckupFmt.Span(p.Start, p.End), 10, secondary: true));
                row.Children.Add(Ui.Label($"{Peak.Title(p.Kind)} {PeakValue(p)}" + (p.App != null ? " · " + p.App : ""), 11, wrap: false));
                return (UIElement)row;
            }).ToList();
            s.Children.Add(Ui.Capped(rows));
        }
        return s;
    }

    static string KindName(PeakKind k) => k is PeakKind.Cpu or PeakKind.Gpu ? Peak.Title(k) : Peak.Title(k).ToLower(Culture);

    static string PeakValue(Peak p) => p.Kind switch
    {
        PeakKind.Cpu or PeakKind.Gpu => Fmt.Pct(p.Top), PeakKind.Temp => Fmt.Deg(p.Top), PeakKind.Mem => p.Top >= 4 ? L("critical") : L("high"), _ => Fmt.Rate(p.Top),
    };

    /// <summary>One sentence per kind: how many, and who caused them when an app caused at least two (or it is the only one).</summary>
    public static string PeakSentence(List<Peak> ps, string when)
    {
        var kind = KindName(ps[0].Kind);
        var top = ps.Where(p => p.App != null).GroupBy(p => p.App!).OrderByDescending(g => g.Count()).FirstOrDefault();
        int n = ps.Count;
        if (top == null || (top.Count() < 2 && n > 1)) return n == 1 ? L("1 %@ peak %@", kind, when) : L("%lld %@ peaks %@", n, kind, when);
        if (n == 1) return L("%@ caused the %@ peak %@", top.Key, kind, when);
        return top.Count() == n ? L("%@ caused all %lld %@ peaks %@", top.Key, n, kind, when) : L("%@ caused %lld of %lld %@ peaks %@", top.Key, top.Count(), n, kind, when);
    }

    // ---- Busy moments (§5.6.8) ----

    public static UIElement Busy(CardCtx c)
    {
        var r = c.R;
        var s = new StackPanel { Spacing = 4 };
        s.Children.Add(Ui.CardTitle("", L("Busy moments"), r.Busy.Count > 0 ? 1 : 0));
        if (r.Busy.Count == 0) s.Children.Add(Ui.Calm(L("No busy moments %@", r.When)));
        else s.Children.Add(Ui.Headline(r.Busy.Count.ToString(), L("periods of ≥ 5 min above 85%% CPU %@", r.When), 1));
        foreach (var b in r.Busy) s.Children.Add(Ui.ItemRow(b.App != null ? L("Mostly %@", b.App) : L("Busy"), $"{CheckupFmt.Span(b.Start, b.End)} · {Fmt.Pct(b.Cpu)}"));
        return s;
    }

    // ---- What Windows is doing (§5.7.4) ----

    static List<string>? jobsNow;
    static DateTime jobsAt;

    public static UIElement Background(CardCtx c)
    {
        var body = new StackPanel { Spacing = 4 };
        void Fill()
        {
            body.Children.Clear();
            body.Children.Add(Ui.CardTitle("", L("What Windows is doing")));
            if (jobsNow == null) { body.Children.Add(Ui.Spinner()); return; }
            body.Children.Add(Ui.Label(jobsNow.Count == 0 ? L("Nothing special in the background right now.") : string.Join(". ", jobsNow.Select(Housekeeping.Tr)) + L(" — this passes by itself."), 11));
            if (c.R.Jobs.Count > 0)
            {
                var h = Ui.Small(L("Busy %@", c.R.When)); h.Margin = new Thickness(0, 2, 0, 0);
                body.Children.Add(h);
                foreach (var j in c.R.Jobs) body.Children.Add(Ui.ItemRow(Housekeeping.Tr(j.Job), CheckupFmt.Duration(j.Minutes)));
            }
        }
        async void Load()
        {
            int cores = Environment.ProcessorCount;
            jobsNow = await Task.Run(() => { try { return WindowsJobs.Busy(cores); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return []; } });
            jobsAt = DateTime.Now;
            if (c.Alive()) Fill();
        }
        Fill();
        if (jobsNow == null || DateTime.Now - jobsAt > TimeSpan.FromMinutes(1)) Load();
        return body;
    }

    // ---- Running time (§5.6.5) ----

    public static UIElement Running(CardCtx c)
    {
        var r = c.R;
        double days = Environment.TickCount64 / 86_400_000.0;
        int restart = Prefs.Int("restartDays");
        int tone = restart > 0 && days >= restart ? 1 : 0;
        var s = new StackPanel { Spacing = 4 };
        s.Children.Add(Ui.CardTitle("", L("Running time"), tone));
        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        s.Children.Add(Ui.Headline(CheckupFmt.Duration((int)(Environment.TickCount64 / 60_000)), L("PC on since %@", boot.ToString("ddd d MMM HH:mm", Culture)), tone));
        s.Children.Add(Ui.LineRow(L("Fusebox measuring since"), r.FirstMinute is { } f ? (f.Date == DateTime.Today ? f.ToString("HH:mm", Culture) : f.ToString("ddd d MMM HH:mm", Culture)) : "–"));
        s.Children.Add(Ui.LineRow(L("Measured this period"), CheckupFmt.Duration(r.Minutes)));
        if (CheckupReaderLastUpdate() is { } u) s.Children.Add(Ui.LineRow(L("Last Windows update"), u.ToString("d MMM yyyy", Culture)));
        if (r.SeenLongest.Count > 0)
        {
            s.Children.Add(Ui.Sub(L("Seen active longest")));
            foreach (var a in r.SeenLongest) s.Children.Add(Ui.ItemRow(a.Name, CheckupFmt.Duration(a.Minutes)));
        }
        return s;
    }

    static DateTime? CheckupReaderLastUpdate() { try { return CheckupReader.LastUpdate(); } catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return null; } }

    // ---- Crashes (§5.6.7) ----

    public static UIElement Crashes(CardCtx c)
    {
        var k = c.R.Crashes;
        var s = new StackPanel { Spacing = 4 };
        if (!k.Any)
        {
            s.Children.Add(Ui.CardTitle("", L("Crashes"), 3));
            s.Children.Add(Ui.Calm(L("No apps crashed or stopped responding %@", c.R.When)));
            return s;
        }
        bool bad = k.Apps.Count > 0 || k.BlueScreens > 0;
        s.Children.Add(Ui.CardTitle("", L("Crashes"), bad ? 2 : 1));
        if (k.Apps.Count > 0)
            s.Children.Add(Ui.Headline(k.Apps.Count == 1 ? L("1 app") : L("%lld apps", k.Apps.Count), L("crashed or stopped responding %@", c.R.When), 1));
        var rows = new List<UIElement>();
        foreach (var a in k.Apps)
        {
            if (a.Crashes > 0) rows.Add(Ui.ItemRow(a.App, L("crashed %lld×", a.Crashes) + (a.Loop ? L(", %lld× in one hour", a.LoopCrashes) : "")));
            if (a.Hangs > 0) rows.Add(Ui.ItemRow(a.App, L("stopped responding %lld×", a.Hangs)));
        }
        if (k.OutOfMemory > 0) rows.Add(Ui.ItemRow(L("Out of memory"), L("Windows warned %lld×", k.OutOfMemory)));
        if (k.BlueScreens > 0) rows.Add(Ui.ItemRow(L("Blue screen"), $"{k.BlueScreens}×"));
        if (k.UnexpectedShutdowns > 0) rows.Add(Ui.ItemRow(L("Unexpected shutdown"), $"{k.UnexpectedShutdowns}×"));
        s.Children.Add(Ui.Capped(rows));
        return s;
    }

    // ---- Windows' own stability history (day-one fill) ----

    static List<double>? stability;
    static DateTime stabilityAt;

    public static UIElement Stability(CardCtx c)
    {
        var body = new StackPanel { Spacing = 4 };
        void Fill()
        {
            body.Children.Clear();
            body.Children.Add(Ui.CardTitle("", L("Windows stability")));
            if (stability == null) { body.Children.Add(Ui.Spinner()); return; }
            if (stability.Count == 0) { body.Children.Add(Ui.Small(L("Windows has no stability history on this PC."))); return; }
            double now = stability[^1];
            body.Children.Add(Ui.Headline(Fmt.One(now), L("of 10 — Windows stability index"), now >= 8 ? 3 : now >= 5 ? 1 : 2));
            if (stability.Count >= 2)
            {
                double lo = stability.Min() - 1, hi = stability.Max() + 1;
                var sp = new Sparkline { Height = 28, Stroke = Ui.Blocks ? Ui.OnRow() : Ui.B("FuseboxAccent") };
                sp.SetValues(stability.Select(v => v - lo).ToList(), hi - lo);
                body.Children.Add(sp);
            }
            body.Children.Add(Ui.Small(L("Last 30 days, from Windows' Reliability Monitor")));
        }
        async void Load()
        {
            stability = await Task.Run(() => { try { return Reliability.Daily(CheckupReader.ReliabilityHistory()); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return []; } });
            stabilityAt = DateTime.Now;
            if (c.Alive()) Fill();
        }
        Fill();
        if (stability == null || DateTime.Now - stabilityAt > TimeSpan.FromMinutes(10)) Load();
        return body;
    }

    // ---- Log (§5.6.9) ----

    public static UIElement Log(CardCtx c)
    {
        var r = c.R;
        var s = new StackPanel { Spacing = 5 };
        s.Children.Add(Ui.CardTitle("", L("Log")));
        if (r.Events.Count == 0) s.Children.Add(Ui.Small(L("Nothing unusual happened")));
        else
        {
            var rows = r.Events.Select(e =>
            {
                var g = new Grid { ColumnSpacing = 6 };
                g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var ic = Ui.Icon(e.Glyph, 11, e.Level == 2 ? Ui.Tone(2) : e.Level == 1 ? Ui.Tone(1) : Ui.OnRow(true));
                ic.Width = 14; ic.VerticalAlignment = VerticalAlignment.Top;
                g.Children.Add(ic);
                var t = Ui.Label(e.Text, 12); Grid.SetColumn(t, 1); g.Children.Add(t);
                var sp = Ui.Num(CheckupFmt.Span(e.Start, e.End), 10, secondary: true); sp.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(sp, 2); g.Children.Add(sp);
                return (UIElement)g;
            }).ToList();
            s.Children.Add(Ui.Capped(rows));
        }
        var files = new StackPanel { Spacing = 4, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var f in r.Files.OrderBy(f => f.Name)) files.Children.Add(Ui.LineRow(f.Name, Fmt.Bytes(f.Bytes)));
        files.Children.Add(Ui.Link(L("Open log folder"), () => Shell.OpenFolder(CsvLog.Dir)));
        s.Children.Add(files);
        return s;
    }

    // ---- simple Battery card (§5.6.6, only when "Battery health" is hidden) ----

    public static UIElement BatterySimple(CardCtx c)
    {
        var last = App.Live.Last;
        int? pct = last != null && double.IsFinite(last.BatteryHealth) ? (int)Math.Round(last.BatteryHealth) : null;
        bool worn = pct is < 80;
        var parts = new List<string> { worn ? L("Worn") : L("Good") };
        if (last != null && double.IsFinite(last.BatteryCycles)) parts.Add(L("%lld cycles", (long)last.BatteryCycles));
        if (pct is { } p) parts.Add(L("%lld%% of original capacity", p));
        var s = new StackPanel { Spacing = 5 };
        s.Children.Add(Ui.CardTitle("", L("Battery"), worn ? 1 : 0));
        s.Children.Add(Ui.Label(string.Join(" · ", parts), 11));
        return s;
    }
}
