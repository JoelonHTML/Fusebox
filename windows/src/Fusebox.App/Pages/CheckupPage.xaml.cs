using System.Diagnostics;
using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Platform;
using Fusebox.Platform.Checkup;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>
/// Checkup: everything is read once, on background threads, when the page opens (or on Refresh). Nothing runs on a timer.
/// Each card has its own ProgressRing; the score and "why is my PC slow" are recomputed whenever a card finishes.
/// </summary>
public sealed partial class CheckupPage : Page
{
    sealed class Section
    {
        readonly ProgressRing ring = new() { Width = 16, Height = 16, Visibility = Visibility.Collapsed };
        readonly StackPanel body = new() { Spacing = 8 };
        int pending;
        public Border Card { get; }

        public Section(string title)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 16 });
            head.Children.Add(ring);
            var all = new StackPanel { Spacing = 8 };
            all.Children.Add(head);
            all.Children.Add(body);
            Card = new Border { Child = all, Style = (Style)Application.Current.Resources["FuseboxCard"] };
        }

        public void Reset() { pending = 0; ring.IsActive = false; ring.Visibility = Visibility.Collapsed; }

        public StackPanel Box() { var b = new StackPanel { Spacing = 6 }; body.Children.Add(b); return b; }

        public void Loading(bool on)
        {
            pending = Math.Max(0, pending + (on ? 1 : -1));
            ring.IsActive = pending > 0;
            ring.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    readonly Section secHealth = new(L("Health")), secWhy = new(L("Why is my PC slow?")), secCrash = new(L("Crashes & stability")),
        secStartup = new(L("Startup apps")), secStorage = new(L("Startup & storage")), secSsd = new(L("SSD health")),
        secSleep = new(L("Sleep & wake")), secBattery = new(L("Battery health"));
    readonly StackPanel healthBox, whyBox, stabBox, crashBox, startupBox, driveBox, foldersBox, diskBox, ssdBox, sleepBox, reqBox, batteryBox;
    readonly Button scanBtn, ssdBtn, reqBtn;

    CancellationTokenSource cts = new(), scanCts = null!;
    CheckupInputs inputs = new();
    CheckupResult? result;
    int busy;
    bool scanning;

    public CheckupPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(Header);

        healthBox = secHealth.Box(); whyBox = secWhy.Box();
        stabBox = secCrash.Box(); crashBox = secCrash.Box();
        startupBox = secStartup.Box();
        driveBox = secStorage.Box(); foldersBox = secStorage.Box();
        diskBox = secSsd.Box(); ssdBox = secSsd.Box();
        sleepBox = secSleep.Box(); reqBox = secSleep.Box();
        batteryBox = secBattery.Box();

        scanBtn = Btn(L("Scan large folders"), OnScan);
        ssdBtn = Btn(L("Check SSD health"), OnSsd);
        reqBtn = Btn(L("What keeps it awake?"), OnRequests);
        secStorage.Card.Child.AsStack().Children.Insert(1, scanBtn);   // under the title, above the drive list
        secSsd.Card.Child.AsStack().Children.Add(ssdBtn);
        secSleep.Card.Child.AsStack().Children.Add(reqBtn);
        secStartup.Card.Child.AsStack().Children.Add(Btn(L("Open Startup apps"), () => Open("ms-settings:startupapps")));

        foreach (var s in new[] { secHealth, secWhy, secCrash, secStartup, secStorage, secSsd, secSleep, secBattery }) Sections.Children.Add(s.Card);
        secBattery.Card.Visibility = Visibility.Collapsed;   // shown only when this PC has a battery
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => LoadAll();
    protected override void OnNavigatedFrom(NavigationEventArgs e) => cts.Cancel();

    void OnRefresh(object sender, RoutedEventArgs e) => LoadAll();

    void OnCopy(object sender, RoutedEventArgs e)
    {
        if (result == null) return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(HealthScore.Report(result, DateTime.Now));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        CopyBtn.Content = L("Copied");
        _ = ResetCopy();
    }

    async Task ResetCopy() { await Task.Delay(2000); CopyBtn.Content = L("Copy report"); }

    // ---- loading ----

    void LoadAll()
    {
        cts.Cancel();
        cts = new();
        var ct = cts.Token;
        inputs = new();
        busy = 0;
        scanning = false;
        foreach (var sec in new[] { secHealth, secWhy, secCrash, secStartup, secStorage, secSsd, secSleep, secBattery }) sec.Reset();
        foldersBox.Children.Clear();
        ssdBox.Children.Clear();
        reqBox.Children.Clear();
        scanBtn.Content = L("Scan large folders");
        ssdBtn.IsEnabled = reqBtn.IsEnabled = true;

        // Live numbers (read-only use of LiveHub, no extra measuring).
        var live = App.Live;
        if (live.Cpu.Count > 0) { inputs.CpuAvg = live.Cpu.Average; inputs.CpuMax = live.Cpu.Max; }
        if (live.Last is { RamTotal: > 0 } s && !double.IsNaN(s.RamUsed)) inputs.RamPct = s.RamUsed * 100 / s.RamTotal;

        Start(secHealth, healthBox, ct, () => (CheckupReader.PendingReboot(), CheckupReader.UptimeDays(), CheckupReader.CommitPct()),
            r => { inputs.PendingReboot = r.Item1; inputs.UptimeDays = r.Item2; inputs.CommitPct = double.IsNaN(r.Item3) ? null : r.Item3; });

        Start(secCrash, crashBox, ct, EventLogReader.Crashes, events =>
        {
            var st = CheckupParsers.Summarize(events);
            inputs.Crashes = st;
            crashBox.Children.Add(Text(L("Last 30 days") + $": {st.AppCrashes} " + L("app crashes") + $", {st.Hangs} " + L("hangs") + $", {st.BlueScreens} " + L("blue screens") + $", {st.UnexpectedShutdowns} " + L("unexpected shutdowns")));
            foreach (var e in events.Take(10)) crashBox.Children.Add(Row(null, $"{e.Time.ToString("g", Culture)} · {Describe(e)}"));
        });
        Start(secCrash, stabBox, ct, CheckupReader.StabilityIndex, si =>
        {
            inputs.StabilityIndex = si;
            if (si is { } v) stabBox.Children.Add(Text(L("Windows stability index: %.1f out of 10", v)));
        });

        Start(secStartup, startupBox, ct, CheckupReader.StartupItems, RenderStartup);

        Start(secStorage, driveBox, ct, CheckupReader.Drives, drives =>
        {
            inputs.Drives = drives;
            foreach (var d in drives)
            {
                driveBox.Children.Add(Text(L("Drive %@: %@ free of %@", d.Name, Fmt.Bytes(d.Free), Fmt.Bytes(d.Total))));
                driveBox.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = 100 - d.FreePct });
            }
        });

        Start(secSsd, diskBox, ct, CheckupReader.Disks, disks =>
        {
            inputs.Disks = disks;
            foreach (var d in disks) diskBox.Children.Add(Row(Health(d.Health), $"{d.Name} · {(d.MediaType == "" ? "?" : d.MediaType)} · {d.Health} · {Fmt.Bytes(d.Size)}"));
            if (disks.Count == 0) diskBox.Children.Add(Text(L("No disk information available")));
        });

        Start(secSleep, sleepBox, ct, EventLogReader.Sleep, events =>
        {
            var sl = CheckupParsers.SummarizeSleep(events);
            inputs.Sleep = sl;
            sleepBox.Children.Add(Text(L("Last 7 days") + $": {sl.Sleeps} " + L("times to sleep") + $", {sl.Wakes.Count} " + L("wake-ups") + $", {sl.NightWakes} " + L("at night (00:00–06:00)")));
            foreach (var w in sl.Wakes.Take(10)) sleepBox.Children.Add(Row(w.Time.Hour < 6 ? Level.Warning : null, $"{w.Time.ToString("g", Culture)} · " + L("woke up: %@", w.Source)));
        });

        Start(secBattery, batteryBox, ct, CheckupReader.Battery, b =>
        {
            inputs.Battery = b;
            if (b == null) return;
            secBattery.Card.Visibility = Visibility.Visible;
            batteryBox.Children.Add(b.Percent is { } p
                ? Row(p < 60 ? Level.Bad : p < 80 ? Level.Warning : Level.Good, L("Your battery holds %lld%% of its original charge", p))
                : Text(L("This PC doesn't report its battery capacity")));
            if (b.Cycles is { } c) batteryBox.Children.Add(Text(L("%lld charge cycles", c)));
            batteryBox.Children.Add(Btn(L("Open Battery"), () => Open("ms-settings:batterysaver")));
        });

        Recompute();
    }

    /// <summary>Runs work off the UI thread, then apply on it. A failing reader only fills its own card with the error.</summary>
    async void Start<T>(Section sec, StackPanel box, CancellationToken ct, Func<T> work, Action<T> apply)
    {
        busy++;
        sec.Loading(true);
        box.Children.Clear();
        try
        {
            var r = await Task.Run(work, ct);
            if (ct.IsCancellationRequested) return;
            apply(r);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested) box.Children.Add(Text(L("Couldn't read this: %@", e.Message)));
        }
        finally { if (!ct.IsCancellationRequested) { sec.Loading(false); busy--; } }
        if (!ct.IsCancellationRequested) Recompute();
    }

    // ---- score ----

    void Recompute()
    {
        var r = result = HealthScore.Evaluate(inputs);
        ScoreText.Text = r.Score.ToString();
        ScoreText.Foreground = Br(r.Score >= 80 ? Level.Good : r.Score >= 50 ? Level.Warning : Level.Bad);
        ScoreSub.Text = busy > 0 ? L("Measuring…") : L("out of 100");

        healthBox.Children.Clear();
        foreach (var c in r.Categories)
        {
            healthBox.Children.Add(new TextBlock { Text = $"{c.Name} — {c.Score}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var f in c.Findings) healthBox.Children.Add(Row(f.Level, f.Text));
            foreach (var t in c.Tips) healthBox.Children.Add(TipRow(t));
        }

        whyBox.Children.Clear();
        foreach (var line in HealthScore.WhySlow(inputs)) whyBox.Children.Add(Row(null, line));
        // Tips with a Settings page are shown once in the Health card above; the buttons here are the same ones.
    }

    UIElement TipRow(Tip t)
    {
        var text = Text("→ " + t.Text);
        if (t.Uri == null) return text;
        var sp = new StackPanel { Spacing = 4 };
        sp.Children.Add(text);
        sp.Children.Add(Btn(t.Button ?? L("Open Settings"), () => Open(t.Uri)));
        return sp;
    }

    // ---- startup ----

    void RenderStartup(List<StartupItem> items)
    {
        inputs.StartupEnabled = items.Count(i => i.Enabled);
        if (items.Count == 0) startupBox.Children.Add(Text(L("No startup apps found")));
        for (int idx = 0; idx < items.Count; idx++)
        {
            int at = idx;
            var item = items[idx];
            var name = new StackPanel();
            name.Children.Add(new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            name.Children.Add(new TextBlock { Text = item.Command, Opacity = 0.6, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            UIElement trail;
            if (item.CanToggle)
            {
                var ts = new ToggleSwitch { IsOn = item.Enabled, OnContent = "", OffContent = "", MinWidth = 0 };
                bool revert = false;
                ts.Toggled += async (_, _) =>
                {
                    if (revert) { revert = false; return; }
                    bool on = ts.IsOn;
                    try
                    {
                        if (!await Task.Run(() => CheckupReader.SetStartupEnabled(items[at], on))) return;
                        items[at] = items[at] with { Enabled = on };
                        inputs.StartupEnabled = items.Count(i => i.Enabled);
                        Recompute();
                    }
                    catch (Exception) { revert = true; ts.IsOn = !on; }
                };
                trail = ts;
            }
            else trail = new TextBlock { Text = item.Enabled ? L("All users") : L("All users · off"), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
            startupBox.Children.Add(Line(null, name, trail));
        }
    }

    // ---- large folders ----

    async void OnScan()
    {
        if (scanning) { scanCts.Cancel(); return; }
        scanning = true;
        scanCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var sc = scanCts;
        scanBtn.Content = L("Cancel");
        foldersBox.Children.Clear();
        secStorage.Loading(true);
        try
        {
            var rows = await LargeFolders.ScanAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sc.Token);
            foreach (var r in rows)
            {
                var open = Btn(L("Show in Explorer"), () => Process.Start(new ProcessStartInfo("explorer.exe", "\"" + r.Path + "\"")));
                foldersBox.Children.Add(Line(null, Text($"{r.Path}   {Fmt.Bytes(r.Bytes)}"), open));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { foldersBox.Children.Add(Text(L("Couldn't read this: %@", e.Message))); }
        finally
        {
            sc.Dispose();
            scanning = false;
            scanBtn.Content = L("Scan large folders");
            secStorage.Loading(false);
        }
    }

    // ---- admin-only checks (UAC prompt, only on click) ----

    async void OnSsd()
    {
        ssdBtn.IsEnabled = false;
        secSsd.Loading(true);
        ssdBox.Children.Clear();
        try
        {
            var json = await Elevated.RequestAsync("ssd");
            if (json == null) { ssdBox.Children.Add(Text(L("Couldn't check: administrator permission was declined."))); return; }
            var (disks, counters, err) = CheckupParsers.ParseSsd(json);
            if (err != null) { ssdBox.Children.Add(Text(L("Couldn't read this: %@", err))); return; }
            inputs.Ssd = counters;
            if (disks.Count > 0) inputs.Disks = disks;
            if (counters.Count == 0) ssdBox.Children.Add(Text(L("This disk doesn't report wear or temperature.")));
            foreach (var c in counters)
            {
                var parts = new List<string>();
                if (c.Wear is { } w) parts.Add(L("Wear: %lld%%", w));
                if (c.Temperature is { } t) parts.Add($"{Math.Round(t)} °C");
                if (c.ReadErrors is { } re) parts.Add(L("Read errors: %lld", re));
                if (c.PowerOnHours is { } h) parts.Add(L("Powered on: %lld hours", h));
                ssdBox.Children.Add(Row(c.Wear is >= 90 ? Level.Bad : c.Wear is >= 70 ? Level.Warning : Level.Good, L("Disk %@", c.Disk) + ": " + string.Join(" · ", parts)));
            }
            Recompute();
        }
        catch (Exception e) { ssdBox.Children.Add(Text(L("Couldn't read this: %@", e.Message))); }
        finally { secSsd.Loading(false); ssdBtn.IsEnabled = true; }
    }

    async void OnRequests()
    {
        reqBtn.IsEnabled = false;
        secSleep.Loading(true);
        reqBox.Children.Clear();
        try
        {
            var text = await Elevated.RequestAsync("requests");
            if (text == null) { reqBox.Children.Add(Text(L("Couldn't check: administrator permission was declined."))); return; }
            var reqs = CheckupParsers.ParsePowercfgRequests(text);
            if (reqs.Count == 0) reqBox.Children.Add(Text(L("Nothing is keeping your PC awake right now.")));
            foreach (var r in reqs) reqBox.Children.Add(Row(Level.Warning, $"{r.Section}: {r.Who}" + (r.Reason.Length > 0 ? $" — {r.Reason}" : "")));
            reqBox.Children.Add(Btn(L("Open Power & sleep"), () => Open("ms-settings:powersleep")));
        }
        catch (Exception e) { reqBox.Children.Add(Text(L("Couldn't read this: %@", e.Message))); }
        finally { secSleep.Loading(false); reqBtn.IsEnabled = true; }
    }

    // ---- small UI helpers ----

    static string Describe(EventInfo e) => (e.Provider, e.Id) switch
    {
        ("Application Error", 1000) => L("%@ crashed", e.At(0) ?? "?") + (e.At(3) is { Length: > 0 } m ? $" ({m})" : ""),
        ("Application Hang", 1002) => L("%@ stopped responding", e.At(0) ?? "?"),
        ("Microsoft-Windows-WER-SystemErrorReporting", 1001) => L("Blue screen (Windows restarted after a serious error)"),
        ("Microsoft-Windows-Kernel-Power", 41) => L("Unexpected shutdown (power loss or crash)"),
        _ => e.Provider,
    };

    static Level Health(string h) => h is "Healthy" or "" ? Level.Good : h == "Warning" ? Level.Warning : Level.Bad;

    static void Open(string uri) { try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(uri)); } catch (Exception) { } }

    Brush Br(Level l) => l switch { Level.Good => GoodSwatch.Fill, Level.Warning => WarnSwatch.Fill, _ => BadSwatch.Fill };

    static TextBlock Text(string s) => new() { Text = s, TextWrapping = TextWrapping.Wrap };

    static Button Btn(string content, Action click)
    {
        var b = new Button { Content = content };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A coloured dot (or none) and wrapping text.</summary>
    UIElement Row(Level? l, string text)
    {
        var dot = new TextBlock { Text = "●", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = l == null ? 0.3 : 1, Margin = new Thickness(0, 2, 0, 0) };
        if (l is { } lv) dot.Foreground = Br(lv);
        return Line(dot, Text(text), null);
    }

    static Grid Line(UIElement? lead, UIElement main, UIElement? trail)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (lead != null) { Grid.SetColumn((FrameworkElement)lead, 0); g.Children.Add(lead); }
        Grid.SetColumn((FrameworkElement)main, 1); g.Children.Add(main);
        if (trail != null) { Grid.SetColumn((FrameworkElement)trail, 2); g.Children.Add(trail); }
        return g;
    }
}

static class CheckupPageExtensions
{
    public static StackPanel AsStack(this UIElement? e) => (StackPanel)e!;
}
