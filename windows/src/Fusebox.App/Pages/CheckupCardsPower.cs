using Fusebox.App.Controls;
using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Fusebox.Platform.Checkup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>Battery &amp; power cards (§5.B.5): battery health with trend, what drained the battery, sleep drain, what keeps the PC awake.</summary>
internal static class BatteryHealthCard
{
    static List<BatteryTrend.Point>? trend;
    static BatteryHealth? fromReport;
    static bool loading;
    static DateTime loadedAt = DateTime.MinValue;

    public static int? Percent => Now?.Percent;
    public static int? Cycles => Now?.Cycles;
    public static bool Worn => Now?.Worn ?? false;

    static BatteryHealth? Now
    {
        get
        {
            var l = App.Live.Last;
            if (l == null || !double.IsFinite(l.BatteryHealth)) return fromReport;
            int? cycles = double.IsFinite(l.BatteryCycles) ? (int)l.BatteryCycles : fromReport?.Cycles;
            return new((int)Math.Round(l.BatteryHealth), cycles);
        }
    }

    public static UIElement Build(CardCtx c)
    {
        var body = new StackPanel { Spacing = 5 };
        var host = Ui.Card(15, body);
        void Fill() { if (host.XamlRoot != null) Render(body); }
        Render(body);
        if (!loading && DateTime.Now - loadedAt > TimeSpan.FromMinutes(30)) Load(Fill, c);
        return host;
    }

    static async void Load(Action fill, CardCtx c)
    {
        loading = true;
        var (csv, report) = await Task.Run(() =>
        {
            var points = new List<BatteryTrend.Point>();
            try { var f = Path.Combine(CsvLog.Dir, CsvLog.BatteryFile); if (File.Exists(f)) points = BatteryTrend.ParseCsv(DayLoader.ReadShared(f)); }
            catch (IOException) { }
            // Day one: fewer than two days of our own history → Windows' own battery report (no admin).
            (BatteryHealth?, List<BatteryTrend.Point>)? rep = null;
            if (points.Count < 2) { try { rep = CheckupReader.BatteryReport() is { } r ? (r.Now, r.History) : null; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
            return (points, rep);
        });
        trend = BatteryTrend.Merge(csv, report?.Item2 ?? []);
        fromReport = report?.Item1;
        loadedAt = DateTime.Now; loading = false;
        fill();
        c.Summaries();
    }

    static void Render(StackPanel body)
    {
        body.Children.Clear();
        var now = Now;
        bool worn = now?.Worn ?? false;
        body.Children.Add(Ui.CardTitle("", L("Battery health"), worn ? 1 : 0));
        var small = new List<string> { L("of original capacity") };
        if (now?.Cycles is { } cy) small.Add(L("%lld cycles", cy));
        body.Children.Add(Ui.Headline(now?.Percent is { } p ? Fmt.Pct(p) : "–", string.Join(" · ", small), worn ? 1 : 0));
        if (trend == null) { body.Children.Add(Ui.Spinner()); return; }
        if (trend.Count >= 2)
        {
            double lo = trend.Min(t => t.Pct) - 1, hi = trend.Max(t => t.Pct) + 1;
            var sp = new Sparkline { Height = 28, Stroke = Ui.Blocks ? Ui.OnRow() : Ui.B("FuseboxAccent") };
            sp.SetValues(trend.Select(t => t.Pct - lo).ToList(), hi - lo);
            body.Children.Add(sp);
            body.Children.Add(Ui.Small(BatteryTrend.Summary(trend)));
        }
        else body.Children.Add(Ui.Small(L("Recorded once a day; the trend shows after a few days")));
    }
}

internal static class DrainCard
{
    sealed record Data((DateTime Start, DateTime? End, double? PctStart, double? PctEnd) Window, List<Drain.Share> Shares, bool OnBattery);
    static Data? data;
    static bool loading;
    static DateTime loadedAt = DateTime.MinValue;

    public static UIElement Build(CardCtx c)
    {
        var body = new StackPanel { Spacing = 5 };
        var host = Ui.Card(12, body);
        void Fill() { if (host.XamlRoot != null) Render(body); }
        Render(body);
        if (!loading && DateTime.Now - loadedAt > TimeSpan.FromMinutes(5)) Load(Fill);
        return host;
    }

    static async void Load(Action fill)
    {
        loading = true;
        bool onBattery = App.Live.Last is { OnAc: false };
        var now = DateTime.Now;
        data = await Task.Run(() =>
        {
            try
            {
                var samples = CheckupData.BatterySamples(now);
                if (Drain.Window(samples, onBattery, now) is not { } w) return null;
                var rows = new List<AppsLog.Row>();
                for (var d = w.Start.Date; d <= now.Date; d = d.AddDays(1)) rows.AddRange(DayLoader.Load(d).Apps);
                return new Data(w, Drain.Rank(rows, w.Start, w.End ?? now), onBattery);
            }
            catch (IOException) { return null; }
        });
        loadedAt = DateTime.Now; loading = false;
        fill();
    }

    static void Render(StackPanel body)
    {
        body.Children.Clear();
        body.Children.Add(Ui.CardTitle("", L("What drained my battery")));
        if (loading && data == null) { body.Children.Add(Ui.Spinner()); return; }
        if (data == null || data.Shares.Count == 0) { body.Children.Add(Ui.Small(L("No app data for this time yet"))); return; }
        var top = data.Shares[0];
        string sub = !data.OnBattery ? L("Today · on the charger now")
            : L("Since unplugged %@", CheckupFmt.Day(data.Window.Start)) + (data.Window.End is { } e ? L(" until %@", CheckupFmt.Day(e)) : "")
              + (data.Window.PctStart is { } a ? $" · {Fmt.Pct(a)} → {(data.Window.PctEnd is { } b ? Fmt.Pct(b) : "?")}" : "");
        body.Children.Add(Ui.Headline($"{top.App} {Fmt.Pct(top.Pct)}", sub));
        double max = data.Shares.Max(s => s.Weight);
        foreach (var s in data.Shares.Skip(1))
        {
            var g = new Grid { ColumnSpacing = 6 };
            g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            g.Children.Add(Ui.Label(s.App, 11, wrap: false));
            var bar = Ui.Bar(s.Weight / max); Grid.SetColumn(bar, 1); g.Children.Add(bar);
            var pct = Ui.Num(Fmt.Pct(s.Pct), 11); pct.Width = 38; pct.TextAlignment = TextAlignment.Right; Grid.SetColumn(pct, 2); g.Children.Add(pct);
            body.Children.Add(g);
        }
        body.Children.Add(Ui.Small(L("Share of the CPU time apps used. Windows doesn't report energy per app.")));
    }
}

internal static class SleepCard
{
    static List<SleepSpans.Span>? spans;
    static bool loading;
    static DateTime loadedAt = DateTime.MinValue;

    /// <summary>Loss of the last span with a known loss (group summary), null when none.</summary>
    public static int? LastLoss => spans?.FirstOrDefault(s => s.Loss is > 0)?.Loss is { } l ? (int)l : null;
    public static bool Heavy => spans?.Any(s => s.Heavy) ?? false;

    public static UIElement Build(CardCtx c)
    {
        var body = new StackPanel { Spacing = 5 };
        var host = Ui.Card(13, body);
        void Fill() { if (host.XamlRoot != null) Render(body); }
        Render(body);
        if (!loading && DateTime.Now - loadedAt > TimeSpan.FromHours(1)) Load(Fill, c);
        return host;
    }

    static async void Load(Action fill, CardCtx c)
    {
        loading = true;
        var now = DateTime.Now;
        spans = await Task.Run(() =>
        {
            try
            {
                var events = EventLogReader.Sleep();
                return SleepSpans.Build(events, CheckupData.BatterySamples(now), 4).Where(s => s.Wake >= now.AddDays(-2)).ToList();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return []; }
        });
        loadedAt = DateTime.Now; loading = false;
        fill();
        c.Summaries();
    }

    static void Render(StackPanel body)
    {
        body.Children.Clear();
        body.Children.Add(Ui.CardTitle("", L("Battery use while asleep"), Heavy ? 1 : 0));
        if (spans == null) { body.Children.Add(Ui.Spinner()); return; }
        if (spans.Count == 0) { body.Children.Add(Ui.Small(L("No sleep longer than 30 minutes in the last 2 days"))); return; }
        var known = spans.FirstOrDefault(s => s.Loss != null);
        if (known != null)
            body.Children.Add(Ui.Headline(known.Loss > 0 ? "−" + Fmt.Pct(known.Loss!.Value) : "0%", L("during the last sleep on battery (%@)", Fmt.Duration(known.Minutes)), known.Heavy ? 1 : 0));
        foreach (var s in spans)
        {
            var v = new StackPanel { Spacing = 1 };
            var g = new Grid { ColumnSpacing = 6 };
            g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            left.Children.Add(Ui.Label($"{CheckupFmt.Day(s.Sleep)} → {CheckupFmt.Day(s.Wake)}", 11, wrap: false));
            left.Children.Add(Ui.Label(Fmt.Duration(s.Minutes), 11, secondary: true, wrap: false));
            g.Children.Add(left);
            var loss = Ui.Num(s.Charger ? L("on charger") : s.Loss is { } l ? (l > 0 ? "−" + Fmt.Pct(l) : "0%") : "–", 11, color: s.Heavy ? Ui.Tone(1, false) : null);
            Grid.SetColumn(loss, 1); g.Children.Add(loss);
            v.Children.Add(g);
            v.Children.Add(Ui.Label(L("Woken by %@", s.Reason), 10, secondary: true));
            body.Children.Add(v);
        }
    }
}

internal static class AwakeCard
{
    static List<AwakeRequests.Hold>? holds;
    static bool asking, declined;

    public static UIElement Build(CardCtx c)
    {
        var body = new StackPanel { Spacing = 5 };
        var host = Ui.Card(14, body);
        void Fill() { if (host.XamlRoot != null) Render(body, () => Run(Fill)); }
        Render(body, () => Run(Fill));
        return host;
    }

    static async void Run(Action fill)
    {
        if (asking) return;
        asking = true; declined = false; fill();
        // powercfg /requests needs administrator rights: a UAC prompt, only when the user asks.
        var text = await Elevated.RequestAsync("requests");
        asking = false;
        if (text == null) declined = true; else holds = AwakeRequests.From(CheckupParsers.ParsePowercfgRequests(text));
        fill();
    }

    static void Render(StackPanel body, Action run)
    {
        body.Children.Clear();
        bool screen = holds?.Any(h => h.Text == L("keeping the screen on")) ?? false;
        body.Children.Add(Ui.CardTitle(screen ? "" : "", L("What keeps my PC awake"), 0,
            holds != null ? Ui.IconButton("", L("Check again"), run) : null));
        if (asking) { body.Children.Add(Ui.Spinner()); return; }
        if (holds == null)
        {
            body.Children.Add(Ui.Small(declined ? L("Needs administrator permission") : L("Windows lists this only with administrator permission, so Fusebox asks first.")));
            var b = Ui.Pill(L("Check now"), Ui.OnRow(), run); b.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(b);
            return;
        }
        if (holds.Count == 0) { body.Children.Add(Ui.Small(L("Nothing: your PC can sleep normally"))); return; }
        foreach (var h in holds)
        {
            var g = new Grid { ColumnSpacing = 6 };
            g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(Ui.Label(h.App, 11, wrap: false));
            var t = Ui.Label(h.Text, 10, secondary: true, wrap: false); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(t, 1); g.Children.Add(t);
            body.Children.Add(g);
        }
    }
}
