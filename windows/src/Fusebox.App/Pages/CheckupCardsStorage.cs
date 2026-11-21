using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Platform;
using Fusebox.Platform.Checkup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>The "Startup &amp; storage" cards (§5.B.1–5.B.4): startup items with switches, disk usage, SSD health, SSD speed test.</summary>
internal static class StartupCard
{
    static List<StartupItem>? items;
    static bool loading;
    static Action? redraw;

    public static int? Count => items == null ? null : StartupParse.CountAtLogin(items);
    public static int Orphans => items?.Where(i => i.Orphan).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0;

    public static UIElement Build(CardCtx c)
    {
        var body = new StackPanel { Spacing = 5 };
        var host = Ui.Card(9, body);
        void Fill() { if (host.XamlRoot != null) Render(body, c, Fill); }
        redraw = Fill;
        Render(body, c, Fill);
        if (items == null && !loading) Load(c);
        return host;
    }

    static async void Load(CardCtx c)
    {
        loading = true;
        try { items = await Task.Run(StartupScanner.Scan); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { items = []; }
        loading = false;
        redraw?.Invoke();
        c.Summaries();
    }

    static void Render(StackPanel body, CardCtx c, Action again)
    {
        body.Children.Clear();
        int orphans = Orphans;
        body.Children.Add(Ui.CardTitle("", L("Startup & background items"), orphans > 0 ? 1 : 0,
            Ui.Pill(L("Open Startup apps"), Ui.OnRow(), () => Shell.Launch("ms-settings:startupapps"))));
        if (items == null) { body.Children.Add(Ui.Spinner()); return; }
        int n = StartupParse.CountAtLogin(items);
        body.Children.Add(Ui.Headline(n.ToString(), n == 1 ? L("app starts at login") : L("apps start at login")));
        body.Children.Add(Ui.Small(L("Apps from other makers that start with Windows or wait in the background. Turn off what you don't use.")));

        var gone = StartupParse.Merge(items.Where(i => i.Orphan));
        if (gone.Count > 0)
        {
            var h = Ui.Label(L("Left behind by deleted apps"), 11, bold: true); h.Foreground = Ui.Tone(1, false); h.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(h);
            body.Children.Add(Ui.Small(L("These try to start every time but their app is gone. Safe to remove.")));
            foreach (var o in gone) body.Children.Add(Row(o, c, again, orphan: true));
        }
        Group(body, L("Apps that start at login"), StartupParse.Merge(items.Where(i => !i.Orphan && i.AtLogin)), c, again);
        Group(body, L("Background helpers"), StartupParse.Merge(items.Where(i => !i.Orphan && !i.AtLogin)), c, again);
    }

    static void Group(StackPanel body, string title, List<StartupItem> list, CardCtx c, Action again)
    {
        var h = Ui.Label(title, 11, bold: true); h.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(h);
        if (list.Count == 0) { body.Children.Add(Ui.Small(L("None"))); return; }
        body.Children.Add(Ui.Capped(list.Select(i => (UIElement)Row(i, c, again, false)).ToList()));
    }

    static Grid Row(StartupItem it, CardCtx c, Action again, bool orphan)
    {
        var g = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < 6; i++) g.ColumnDefinitions.Add(new() { Width = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        int col = 0;
        void Add(FrameworkElement e) { Grid.SetColumn(e, col++); g.Children.Add(e); }
        if (orphan) { var w = Ui.Icon("", 11, Ui.Tone(1)); Add(w); } else Add(Ui.Dot(it.Running));
        var name = Ui.Label(it.Name, 11, wrap: false);
        Add(name);
        var word = Ui.Label(!it.Enabled ? L("off") : it.Running ? L("running") : it.Scope == StartupScope.User ? L("you") : L("all users"), 10, secondary: true, wrap: false);
        word.VerticalAlignment = VerticalAlignment.Center;
        Add(word);
        var usage = StartupParse.Usage(it.Cpu, it.MemMb);
        if (usage.Length > 0)
        {
            var u = Ui.Num(usage, 11, color: StartupParse.Heavy(it.Cpu) ? Ui.Tone(1, false) : Ui.OnRow(true));
            Ui.Tip(u, L("Average since it started (memory: now)"));
            Add(u);
        }
        else col++;
        if (orphan && it.Scope != StartupScope.User)
        {
            var need = Ui.Label(L("needs administrator permission"), 10, secondary: true, wrap: false);
            Add(need);
        }
        else col++;
        if (it.Members.Any(m => m.CanToggle))
        {
            var sw = new ToggleSwitch { IsOn = it.Enabled, OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(0, -6, -8, -6), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(sw, it.Name);
            bool busy = false;
            bool elevated = it.Members.Any(m => m.CanToggleElevated);
            if (elevated) Ui.Tip(sw, L("Needs administrator permission"));
            sw.Toggled += async (_, _) =>
            {
                if (busy || sw.IsOn == it.Enabled) return;
                busy = true;
                bool on = sw.IsOn, ok = true;
                foreach (var m in it.Members.Where(m => m.CanToggle))
                {
                    if (m.CanToggleDirect) { try { ok &= StartupScanner.SetDirect(m, on); } catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { ok = false; } }
                    else ok &= await StartupScanner.SetElevatedAsync(m, on);
                    if (ok) m.Enabled = on;
                }
                if (ok) it.Enabled = on;
                else sw.IsOn = it.Enabled;
                busy = false;
                again();
                c.Summaries();
            };
            Add(sw);
        }
        Ui.Tip(g, it.Command + (it.Orphan && it.Exe != null ? "\n" + L("Missing: %@", it.Exe) : ""));
        return g;
    }
}

internal static class DiskCard
{
    static DiskScanner.Result? last;
    static bool scanning;
    static (int Done, int Total, string Title) progress;
    static CancellationTokenSource? cts;
    static Action? redraw;
    static string relativeBase = "";

    public static double FreeGb => Free.Free / 1e9;
    public static bool Low => Free.Total > 0 && Free.Free * 10.0 < Free.Total;

    static (long Free, long Total) Free
    {
        get
        {
            try { var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!); return (d.AvailableFreeSpace, d.TotalSize); }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return (0, 0); }
        }
    }

    public static void Cancel() { cts?.Cancel(); }

    public static UIElement Build(CardCtx c)
    {
        var body = new StackPanel { Spacing = 5 };
        var host = Ui.Card(10, body);
        void Fill() { if (host.XamlRoot != null) Render(body, c); }
        redraw = Fill;
        Render(body, c);
        return host;
    }

    static void Render(StackPanel body, CardCtx c)
    {
        body.Children.Clear();
        var (free, total) = Free;
        var btn = Ui.Pill(last == null ? L("Find large folders") : L("Check again"), Ui.OnRow(), () => Scan(c));
        btn.IsEnabled = !scanning;
        body.Children.Add(Ui.CardTitle("", L("What takes my disk space?"), Low ? 1 : 0, btn));
        if (total > 0) body.Children.Add(Ui.Headline(L("%lld GB free", (long)(free / 1e9)), L("of %lld GB", (long)(total / 1e9)), Low ? 1 : 0));
        if (scanning)
        {
            body.Children.Add(Ui.Progress(progress.Done, Math.Max(progress.Total, 1)));
            if (progress.Title.Length > 0) { var l = Ui.Label(L("Checking %@…", progress.Title), 11, secondary: true, wrap: false); body.Children.Add(l); }
            return;
        }
        if (last == null) { body.Children.Add(Ui.Small(L("Finds the biggest folders in your user folder. Nothing is deleted."))); return; }
        double largest = last.Rows.Count > 0 ? last.Rows.Max(r => r.Bytes) : 1;
        foreach (var r in last.Rows)
        {
            var rel = r.Key ?? "";
            var advice = DiskAdvice.For(rel);
            var inside = DiskAdvice.BiggestInside(r, rel);
            var v = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 2) };
            var top = new Grid { ColumnSpacing = 6 };
            for (int i = 0; i < 5; i++) top.ColumnDefinitions.Add(new() { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var title = Ui.Label(r.Title, 11, wrap: false); top.Children.Add(title);
            int col = 1;
            if (advice is { } a)
            {
                var tag = Ui.Tag(DiskAdvice.Label(a.V), a.V switch { Verdict.Safe => Ui.Tone(3), Verdict.Careful => Ui.Tone(1), _ => Ui.OnRow(true) });
                Grid.SetColumn(tag, col); top.Children.Add(tag);
            }
            col = 2;
            var bar = Ui.Bar(r.Bytes / largest); Grid.SetColumn(bar, col++); top.Children.Add(bar);
            var size = Ui.Num(Fmt.Bytes(r.Bytes), 11); size.Width = 58; size.TextAlignment = TextAlignment.Right; Grid.SetColumn(size, col++); top.Children.Add(size);
            var path = r.Path;
            var show = Ui.Pill(L("Show in Explorer"), Ui.OnRow(), () => Shell.Reveal(path)); Grid.SetColumn(show, col); top.Children.Add(show);
            v.Children.Add(top);
            v.Children.Add(Ui.Label(advice?.Text ?? (inside == null ? DiskAdvice.Fallback : ""), 10, secondary: true));
            if (inside != null) { var ins = Ui.Label(inside, 10, secondary: true); ins.MaxLines = 2; v.Children.Add(ins); }
            body.Children.Add(v);
        }
        if (last.NotCounted.Count > 0)
            body.Children.Add(Ui.Small(L("Not counted (Windows protects these): %@. Sizes are too small without administrator rights.", string.Join(", ", last.NotCounted))));
    }

    static async void Scan(CardCtx c)
    {
        if (scanning) return;
        scanning = true; progress = (0, 1, "");
        cts = new();
        var token = cts.Token;
        redraw?.Invoke();
        try
        {
            var roots = await Task.Run(DiskScanner.Plan, token);
            var prog = new Progress<(int, int, string)>(p => { progress = p; redraw?.Invoke(); });
            last = await DiskScanner.ScanAsync(roots, prog, token);
        }
        catch (OperationCanceledException) { }   // leaving the page cancels; a half result is discarded
        scanning = false;
        redraw?.Invoke();
        c.Summaries();
    }
}

internal static class SsdHealthCard
{
    static List<DiskInfo>? disks;
    static DateTime disksAt = DateTime.MinValue;
    static (List<DiskReliability> Counters, string? Error)? detail;
    static bool loading, asking;

    public static UIElement Build(CardCtx c)
    {
        var host = new StackPanel { Spacing = 0 };
        void Fill() { if (host.XamlRoot != null) Render(host, Fill); }
        Render(host, Fill);
        if ((disks == null || DateTime.Now - disksAt > TimeSpan.FromHours(1)) && !loading) Load(Fill);
        return host;
    }

    static async void Load(Action fill)
    {
        loading = true;
        try { disks = await Task.Run(CheckupReader.Disks); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { disks = []; }
        disksAt = DateTime.Now; loading = false;
        fill();
    }

    static void Render(StackPanel host, Action fill)
    {
        host.Children.Clear();
        // Shown only when Windows reports a health status for an SSD without admin rights; otherwise nothing at all.
        var ssds = (disks ?? []).Where(d => d.MediaType == "SSD" && d.Health.Length > 0).ToList();
        if (ssds.Count == 0) return;
        var rel = detail?.Counters ?? [];
        bool warn = ssds.Any(d => d.Health != "Healthy") || rel.Any(r => r.Wear >= 80 || r.ReadErrors > 0);
        var s = new StackPanel { Spacing = 4 };
        s.Children.Add(Ui.CardTitle("", L("SSD health"), warn ? 1 : 3));
        var small = new List<string>();
        if (rel.Count > 0)
        {
            var r = rel[0];
            if (r.Wear is { } w) small.Add(L("Wear %lld%%", w));
            if (r.PowerOnHours is { } h) small.Add(L("%@ hours on", h.ToString("#,0", CultureInfoEn)));
            if (r.ReadErrors is > 0) small.Add(L("%lld read errors", r.ReadErrors.Value));
        }
        else small.Add(ssds.Count == 1 ? L("Windows reports this drive as healthy") : L("Windows reports these drives as healthy"));
        if (warn && rel.Count == 0) small[0] = string.Join(", ", ssds.Where(d => d.Health != "Healthy").Select(d => L("%@ reports its health as %@", d.Name, d.Health)));
        s.Children.Add(Ui.Headline(warn ? L("Check soon") : L("OK"), string.Join(" · ", small), warn ? 1 : 3));
        if (warn) s.Children.Add(Ui.Label(L("The SSD is wearing out or reports errors. Keep a recent backup."), 11));
        if (detail == null)
        {
            var more = Ui.Pill(L("Show details"), Ui.OnRow(), async () =>
            {
                if (asking) return;
                asking = true;
                var json = await Elevated.RequestAsync("ssd");   // UAC; null = declined
                asking = false;
                if (json != null) { var p = CheckupParsers.ParseSsd(json); detail = (p.Counters, p.Error); }
                fill();
            }, L("Needs administrator permission"));
            more.HorizontalAlignment = HorizontalAlignment.Left;
            s.Children.Add(more);
        }
        host.Children.Add(Ui.Card(11, s));
    }

    static readonly System.Globalization.CultureInfo CultureInfoEn = System.Globalization.CultureInfo.GetCultureInfo("en-US");
}

internal static class SsdSpeedCard
{
    static bool running;
    static double progress;
    static string? note;
    static CancellationTokenSource? cts;
    static Action? redraw;

    public static void Cancel() { cts?.Cancel(); }

    public static UIElement Build(CardCtx c)
    {
        var body = new StackPanel { Spacing = 5 };
        var host = Ui.Card(12, body);
        void Fill() { if (host.XamlRoot != null) Render(body); }
        redraw = Fill;
        Render(body);
        return host;
    }

    static void Render(StackPanel body)
    {
        body.Children.Clear();
        var last = SsdBenchResult.Parse(Prefs.Str("ssdBenchLast"));
        int tone = last == null ? 0 : last.Slow ? 1 : 3;
        var btn = running ? Ui.Pill(L("Stop"), Ui.OnRow(), () => cts?.Cancel()) : Ui.Pill(last == null ? L("Test speed") : L("Test again"), Ui.OnRow(), Start);
        body.Children.Add(Ui.CardTitle("", L("SSD speed"), tone, btn));
        if (running)
        {
            body.Children.Add(Ui.Progress(progress));
            body.Children.Add(Ui.Small(progress < 0.5 ? L("Writing a test file…") : L("Reading it back…")));
        }
        else if (last != null)
        {
            body.Children.Add(Ui.Headline(last.Slow ? L("Slower than usual") : L("As fast as expected"), L("Read %@ · Write %@ · %@", CheckupFmt.Speed(last.ReadMBps), CheckupFmt.Speed(last.WriteMBps),
                last.When.ToString("d MMM yyyy HH:mm", Culture)), tone));
            if (last.Slow) body.Children.Add(Ui.Label(L("The disk may be almost full or busy. Try again when nothing else is running."), 11));
        }
        else body.Children.Add(Ui.Small(L("Writes and reads 1 GB in a temporary file (a few seconds), then deletes it.")));
        if (note != null) { var n = Ui.Label(note, 11); n.Foreground = Ui.Blocks ? Ui.OnRow() : Ui.B("FuseboxWarn"); body.Children.Add(n); }
    }

    static async void Start()
    {
        if (running) return;
        running = true; progress = 0; note = null;
        cts = new();
        redraw?.Invoke();
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        try
        {
            var r = await SsdBench.RunAsync(p => ui.TryEnqueue(() => { progress = p; redraw?.Invoke(); }), cts.Token);
            Prefs.Set("ssdBenchLast", r.ToJson());
        }
        catch (OperationCanceledException) { }
        catch (SsdBench.NotEnoughSpace) { note = L("Needs at least 5 GB of free space."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { note = L("The test couldn't finish."); }
        running = false;
        redraw?.Invoke();
    }
}
