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
        var body = new StackPanel { Spacing = 6 };
        void Fill() { if (body.XamlRoot != null) Render(body, c, Fill); }
        redraw = Fill;
        Render(body, c, Fill);
        if (items == null && !loading) Load(c);
        return body;
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
        body.Children.Add(Ui.CardTitle("\uE7BA", L("Startup & background items"), Orphans > 0 ? 1 : 0));
        if (items == null) { body.Children.Add(Ui.Spinner()); return; }
        int n = StartupParse.CountAtLogin(items);
        body.Children.Add(Ui.Label($"{n} {(n == 1 ? L("app starts at login") : L("apps start at login"))}", 13, bold: true));
        body.Children.Add(Ui.Small(L("Apps from other makers that start with Windows or wait in the background. Turn off what you don't use.")));

        var gone = StartupParse.Merge(items.Where(i => i.Orphan));
        if (gone.Count > 0)
        {
            var h = Ui.Label(L("Left behind by deleted apps"), 12, bold: true); h.Foreground = Ui.Tone(1, false); h.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(h);
            body.Children.Add(Ui.Small(L("These try to start every time but their app is gone. Safe to remove.")));
            foreach (var o in gone) body.Children.Add(Row(o, c, again, orphan: true));
        }
        Group(body, L("Apps that start at login"), StartupParse.Merge(items.Where(i => !i.Orphan && i.AtLogin)), c, again);
        Group(body, L("Background helpers"), StartupParse.Merge(items.Where(i => !i.Orphan && !i.AtLogin)), c, again);
        var open = Ui.Link(L("Open Startup apps"), () => Shell.Launch("ms-settings:startupapps"));
        open.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(open);
    }

    static void Group(StackPanel body, string title, List<StartupItem> list, CardCtx c, Action again)
    {
        var h = Ui.Label(title, 12, bold: true); h.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(h);
        if (list.Count == 0) { body.Children.Add(Ui.Small(L("None"))); return; }
        body.Children.Add(Ui.Capped(list.Select(i => (UIElement)Row(i, c, again, false)).ToList(), spacing: 8));
    }

    /// <summary>One line like a Windows Settings toggle row: (warning icon) name with usage below it, state word, switch, all centred.</summary>
    static Grid Row(StartupItem it, CardCtx c, Action again, bool orphan)
    {
        var g = new Grid { ColumnSpacing = 10, MinHeight = 32 };
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        void Add(FrameworkElement e, int col) { e.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(e, col); g.Children.Add(e); }
        if (orphan) Add(Ui.Icon("\uE7BA", 12, Ui.Tone(1)), 0);
        var names = new StackPanel { Spacing = 1 };
        names.Children.Add(Ui.Label(it.Name, 12, wrap: false));
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var usage = StartupParse.Usage(it.Cpu, it.MemMb);
        if (usage.Length > 0)
        {
            var u = Ui.Num(usage, 11, color: StartupParse.Heavy(it.Cpu) ? Ui.Tone(1, false) : Ui.OnRow(true));
            Ui.Tip(u, L("Average since it started (memory: now)"));
            sub.Children.Add(u);
        }
        if (orphan && it.Scope != StartupScope.User) sub.Children.Add(Ui.Label(L("needs administrator permission"), 11, secondary: true, wrap: false));
        if (sub.Children.Count > 0) names.Children.Add(sub);
        Add(names, 1);
        Add(Ui.Label(!it.Enabled ? L("off") : it.Running ? L("running") : it.Scope == StartupScope.User ? L("you") : L("all users"), 11, secondary: true, wrap: false), 2);
        if (it.Members.Any(m => m.CanToggle))
        {
            var sw = new ToggleSwitch { IsOn = it.Enabled, OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(0, -6, -8, -6) };
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
            Add(sw, 3);
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

    /// <summary>Free/total GB shown in the card and the group summary: for Today the report's last logged minute (the same number the
    /// disk tip uses), else the drive now.</summary>
    public static (double Free, double Total) Gb(Report r)
    {
        if (r.Period == Period.Today && double.IsFinite(r.DiskFreeGb) && r.DiskTotalGb > 0) return (r.DiskFreeGb, r.DiskTotalGb);
        var (free, total) = Free;
        return (free / 1e9, total / 1e9);
    }

    public static bool Low(Report r) { var (free, total) = Gb(r); return total > 0 && free * 10 < total; }

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
        var body = new StackPanel { Spacing = 6 };
        void Fill() { if (body.XamlRoot != null) Render(body, c); }
        redraw = Fill;
        Render(body, c);
        return body;
    }

    static void Render(StackPanel body, CardCtx c)
    {
        body.Children.Clear();
        var (free, total) = Gb(c.R);
        bool low = total > 0 && free * 10 < total;
        body.Children.Add(Ui.CardTitle("\uE7BA", L("What takes my disk space?"), low ? 1 : 0));
        if (total > 0) body.Children.Add(Ui.Headline(L("%lld GB free", (long)free), L("of %lld GB", (long)total), low ? 1 : 0));
        if (scanning)
        {
            body.Children.Add(Ui.Progress(progress.Done, Math.Max(progress.Total, 1)));
            if (progress.Title.Length > 0) { var l = Ui.Label(L("Checking %@…", progress.Title), 11, secondary: true, wrap: false); body.Children.Add(l); }
            return;
        }
        if (last == null) body.Children.Add(Ui.Small(L("Finds the biggest folders in your user folder. Nothing is deleted.")));
        else
        {
            double largest = last.Rows.Count > 0 ? last.Rows.Max(r => r.Bytes) : 1;
            var rows = new StackPanel { Spacing = 2, Margin = new Thickness(-8, 4, -8, 0) };
            foreach (var r in last.Rows) rows.Children.Add(FolderRow(r, largest));
            body.Children.Add(rows);
            if (last.NotCounted.Count > 0)
                body.Children.Add(Ui.Small(L("Not counted (Windows protects these): %@. Sizes are too small without administrator rights.", string.Join(", ", last.NotCounted))));
        }
        body.Children.Add(Ui.Link(last == null ? L("Find large folders") : L("Check again"), () => Scan(c)));
    }

    /// <summary>One folder: the whole row reveals it in Explorer (click, Enter/Space; tooltip). Name + verdict badge, then bar + size, then advice.</summary>
    static Button FolderRow(DiskRow r, double largest)
    {
        var rel = r.Key ?? "";
        var advice = DiskAdvice.For(rel);
        var inside = DiskAdvice.BiggestInside(r, rel);
        var v = new StackPanel { Spacing = 3 };
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        top.Children.Add(Ui.Label(r.Title, 12, wrap: false));
        if (advice is { } a)
        {
            var tag = Ui.Tag(DiskAdvice.Label(a.V), a.V switch { Verdict.Safe => Ui.Tone(3), Verdict.Careful => Ui.Tone(1), _ => Ui.OnRow(true) });
            Grid.SetColumn(tag, 1); top.Children.Add(tag);
        }
        v.Children.Add(top);
        var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        size.Children.Add(Ui.Bar(r.Bytes / largest, 120));
        size.Children.Add(Ui.Num(Fmt.Bytes(r.Bytes), 11, secondary: true));
        v.Children.Add(size);
        var text = advice?.Text ?? (inside == null ? DiskAdvice.Fallback : "");
        if (text.Length > 0) v.Children.Add(Ui.Label(text, 11, secondary: true));
        if (inside != null) { var ins = Ui.Label(inside, 11, secondary: true); ins.MaxLines = 2; v.Children.Add(ins); }
        var b = new Button { Content = v, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(4) };
        Ui.Flat(b);
        var path = r.Path;
        b.Click += (_, _) => Shell.Reveal(path);
        Ui.Tip(b, L("Show in Explorer"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"{r.Title}, {Fmt.Bytes(r.Bytes)}. {L("Show in Explorer")}");
        return b;
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
        var body = new StackPanel { Spacing = 0 };
        void Fill() { if (body.XamlRoot != null) Render(body, Fill); }
        Render(body, Fill);
        if ((disks == null || DateTime.Now - disksAt > TimeSpan.FromHours(1)) && !loading) Load(Fill);
        return body;
    }

    static async void Load(Action fill)
    {
        loading = true;
        try { disks = await Task.Run(CheckupReader.Disks); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { disks = []; }
        disksAt = DateTime.Now; loading = false;
        fill();
    }

    static void Render(StackPanel body, Action fill)
    {
        body.Children.Clear();
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
            var more = Ui.Link(L("Show details"), async () =>
            {
                if (asking) return;
                asking = true;
                var json = await Elevated.RequestAsync("ssd");   // UAC; null = declined
                asking = false;
                if (json != null) { var p = CheckupParsers.ParseSsd(json); detail = (p.Counters, p.Error); }
                fill();
            }, L("Needs administrator permission"));
            s.Children.Add(more);
        }
        body.Children.Add(s);
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
        void Fill() { if (body.XamlRoot != null) Render(body); }
        redraw = Fill;
        Render(body);
        return body;
    }

    static void Render(StackPanel body)
    {
        body.Children.Clear();
        var last = SsdBenchResult.Parse(Prefs.Str("ssdBenchLast"));
        int tone = last == null ? 0 : last.Slow ? 1 : 3;
        body.Children.Add(Ui.CardTitle("", L("SSD speed"), tone));
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
        body.Children.Add(running ? Ui.Link(L("Stop"), () => cts?.Cancel(), danger: true) : Ui.Link(last == null ? L("Test speed") : L("Test again"), Start));
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
