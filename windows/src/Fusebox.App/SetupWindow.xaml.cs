using Fusebox.App.Settings;
using Fusebox.Core;
using Fusebox.Core.Alerts;
using Fusebox.Core.Look;
using Fusebox.Core.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>
/// First-run setup (FEATURES.md §11.7): 600×540, "Step n of 12" with a progress bar, one question per step, the same cards, tiles and
/// drop-downs as Settings.
/// Every choice is applied at once (the main window changes behind it). Closing the window cancels: the starting settings come back,
/// but setupDone is set so it isn't asked again at every start. "Skip setup" and "Start using Fusebox" keep the choices.
/// </summary>
public sealed partial class SetupWindow : Window
{
    const int Steps = 12;
    int step;
    bool finished;
    string? importMessage;
    DispatcherQueueTimer? permissionTimer;
    // The starting point, for cancel. Export() leaves out the Local keys (they never travel), so the two that setup changes are kept by hand.
    readonly string snapshot = Prefs.Store.Export();
    readonly long keepDays = Prefs.Long("keepDays");
    readonly string pingHost = Prefs.Str("pingHost");
    readonly bool login = StartAtLogin.Enabled;

    public SetupWindow()
    {
        InitializeComponent();
        Title = L("Set up Fusebox");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Fusebox.ico"));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsResizable = false; p.IsMaximizable = false; }
        ThemeService.Attach(this, themed: false);
        double s = Win32.Scale(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int w = (int)(600 * s), h = (int)(540 * s);
        AppWindow.MoveAndResize(new(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        NextButton.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter });
        Closed += (_, _) =>
        {
            permissionTimer?.Stop();
            if (App.Quitting) return;   // quitting: setup stays "not done" only if it never was
            if (!finished) Restore();
            Prefs.Set("setupDone", true);
        };
        Show();
    }

    void Restore()
    {
        Prefs.Store.Import(snapshot);
        Prefs.Set("keepDays", keepDays);
        Prefs.Set("pingHost", pingHost);
        StartAtLogin.Set(login);
    }

    void Finish() { finished = true; Prefs.Set("setupDone", true); Close(); }

    void Skip_Click(object sender, RoutedEventArgs e) => Finish();
    void Back_Click(object sender, RoutedEventArgs e) { step--; Show(); }
    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (step < Steps - 1) { step++; Show(); }
        else Finish();
    }

    void Show()
    {
        permissionTimer?.Stop();
        BodyHost.Children.Clear();
        StepText.Text = L("Step %lld of %lld", step + 1, Steps);
        Progress.Value = step + 1;
        AppIcon.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Content = L("Skip setup");
        BackButton.Content = L("Back");
        BackButton.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = step == Steps - 1 ? L("Start using Fusebox") : L("Next");
        (TitleText.Text, HelpText.Text) = step switch
        {
            0 => (L("Welcome to Fusebox"), L("Fusebox shows how busy your PC is: processor, memory, storage, internet and battery.") + "\n" + L("In a small window, in your system tray, or both.")),
            1 => (L("Pick a style"), L("This is how the Fusebox window looks. You can switch any time.")),
            2 => (L("How big should the text be?"), L("Pick what reads comfortably from where you sit.")),
            3 => (L("What do you want to see?"), L("Turn measurements on or off. Drag them to change the order.")),
            4 => (L("Percent or real values?"), L("Percent is quick to read. Real values tell you exactly how much.")),
            5 => (L("Show graphs when you make the window bigger?"), L("Graphs show the last few minutes. They only appear when there's room.")),
            6 => (L("What should the tray icon show?"), L("Something small next to the clock, so you can check at a glance.")),
            7 => (L("How should Fusebox start?"), L("Fusebox uses very little power, so it's fine to keep it running.")),
            8 => (L("Should Fusebox warn you?"), L("A notification when something needs attention, like your PC getting too hot.")),
            9 => (L("Data and privacy"), L("Everything stays on your PC.")),
            10 => (L("Notifications"), L("Windows shows Fusebox's notifications without asking. If they are off, you can allow them again in Windows Settings.")),
            _ => (L("You're all set!"), L("Here's what you picked.")),
        };
        switch (step)
        {
            case 0: Welcome(); break;
            case 1: BodyHost.Children.Add(Parts.ThemeTiles()); break;
            case 2: TextAndAccent(); break;
            case 3: BodyHost.Children.Add(MetricsEditor.Create("metrics", subtitles: true)); break;
            case 4:
                foreach (var m in new[] { Metric.Cpu, Metric.Ram, Metric.Ssd }) BodyHost.Children.Add(Parts.UnitPicker(m));
                Footer(BodyHost, L("Tip: you can also click a number on the Live page to switch."));
                break;
            case 5: BodyHost.Children.Add(Parts.GraphCards()); break;
            case 6: Tray(); break;
            case 7: Start(); break;
            case 8: Warnings(); break;
            case 9: Data(); break;
            case 10: Permission(); break;
            default: Summary(); break;
        }
    }

    // ---- steps ----

    void Welcome()
    {
        var clock = Kit.Row(8, new FontIcon { Glyph = "", FontSize = 16 }, new TextBlock { Text = L("This takes about a minute.") });
        clock.Margin = new Thickness(2, 4, 0, 8);
        BodyHost.Children.Add(clock);
        var message = Kit.CaptionText("");
        var import = new HyperlinkButton { Content = L("Import settings from a friend…"), Padding = new Thickness(2, 4, 2, 4) };
        import.Click += async (_, _) =>
        {
            if (await Parts.ImportSettings(this) is not { } r) return;
            importMessage = r.Text;
            if (r.Ok) { step = Steps - 1; Show(); }
            else message.Text = r.Text;
        };
        BodyHost.Children.Add(import);
        BodyHost.Children.Add(message);
    }

    void TextAndAccent()
    {
        BodyHost.Children.Add(Parts.TextSizeCards());
        var accent = Card("Accent color", "", Parts.AccentPicker());
        accent.Margin = new Thickness(0, 12, 0, 0);
        BodyHost.Children.Add(accent);
        Footer(BodyHost, L("Optional: your own accent color. \"Default\" uses the style's color."));
    }

    void Tray()
    {
        BodyHost.Children.Add(Parts.TrayStyleCards());
        var more = new StackPanel { Spacing = 4 };
        void Show()
        {
            more.Children.Clear();
            if (Prefs.Str("menubarStyle") is "off" or "icon") return;
            more.Children.Add(Section("Which measurements? (max. 4)"));
            more.Children.Add(MetricsEditor.Create("menubarMetrics", limit: 4));
        }
        Show();
        Kit.WhilePrefs(more, k => { if (k == "menubarStyle") Show(); });
        BodyHost.Children.Add(more);
    }

    void Start()
    {
        var on = new ToggleSwitch { IsOn = StartAtLogin.Enabled, MinWidth = 0 };
        on.Toggled += (_, _) => StartAtLogin.Set(on.IsOn);
        BodyHost.Children.Add(Card("Open at login", "", on, ""));
        BodyHost.Children.Add(Parts.TaskbarCard());
    }

    void Warnings()
    {
        var kinds = new StackPanel { Spacing = 4 };
        var host = new ContentControl { Content = kinds, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var id in Parts.AlertIds()) kinds.Children.Add(Parts.AlertRow(id, compact: true));
        void On() => host.IsEnabled = Prefs.Bool("notify");
        BodyHost.Children.Add(Card("Show notifications", "", Toggle("notify", On), ""));
        BodyHost.Children.Add(host);
        On();
        Footer(BodyHost, L("You can change the limits later in Settings › Notifications."));
    }

    void Data()
    {
        (long Days, string Label)[] keep = [(7, L("7 days")), (30, L("30 days")), (90, L("90 days")), (0, L("Forever"))];
        var bar = Kit.Pick(keep.Select(k => (k.Days.ToString(), k.Label)).ToArray(), Prefs.Long("keepDays").ToString(), id => Prefs.Set("keepDays", long.Parse(id)));
        BodyHost.Children.Add(Card("Keep measurement history for", "", bar, ""));
        var ping = new ToggleSwitch { IsOn = Prefs.Str("pingHost").Length > 0, MinWidth = 0 };
        ping.Toggled += (_, _) => Prefs.Set("pingHost", ping.IsOn ? "1.1.1.1" : "");
        BodyHost.Children.Add(Card("Measure internet response time", "Every 10 seconds Fusebox makes one tiny connection to 1.1.1.1 to time it. Nothing about you is sent. Off means no internet connections at all.", ping, ""));
    }

    /// <summary>Windows needs no prompt for notifications; this step shows whether they are allowed and links to Windows Settings.</summary>
    void Permission()
    {
        var state = new ContentPresenter();
        var card = Card("Notifications", "Updates, peaks, heat and battery warnings", state, "");
        void Refresh()
        {
            if (Notify.Allowed)
            {
                var ok = Kit.Row(6, new FontIcon { Glyph = "", FontSize = 14, Foreground = Previews.B(new Argb(255, 15, 140, 60)) }, new TextBlock { Text = L("Allowed") });
                state.Content = ok;
            }
            else
            {
                var open = new Button { Content = L("Open Settings") };
                open.Click += (_, _) => Kit.OpenUri("ms-settings:notifications");
                state.Content = Kit.Row(10, new TextBlock { Text = L("Off"), VerticalAlignment = VerticalAlignment.Center }, open);
            }
        }
        Refresh();
        BodyHost.Children.Add(card);
        Footer(BodyHost, L("Fusebox only measures; it never reads, sends or deletes your files."));
        permissionTimer = DispatcherQueue.CreateTimer();
        permissionTimer.Interval = TimeSpan.FromSeconds(2);   // only while this step is open
        permissionTimer.Tick += (_, _) => Refresh();
        permissionTimer.Start();
    }

    void Summary()
    {
        if (importMessage != null) BodyHost.Children.Add(Kit.CaptionText(importMessage));
        void Row(string header, string value) => BodyHost.Children.Add(new SettingsCard { Header = header, Action = new TextBlock { Text = value, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxWidth = 280, TextAlignment = TextAlignment.Right } });
        Row("Look", L("%@, %@ text", Themes.Named(Prefs.Str("theme")).Name, SettingsText.TextSize(Prefs.Double("textScale"))));
        Row("Measurements", SettingsText.MetricList(Prefs.Str("metrics")));
        Row("Tray icon", Prefs.Str("menubarStyle") switch
        {
            "text" => L("Text (CPU 23%)"), "compact" => L("Compact (small symbols)"), "bars" => L("Bars"), "graph" => L("Graphs"), "icon" => L("Just an icon"), _ => L("Don't show"),
        });
        Row("Notifications", Prefs.Bool("notify") ? L("%lld kinds on", SettingsText.KindsOn(Prefs.Bool)) : L("Off"));
        Row("Data", (Prefs.Long("keepDays") == 0 ? L("History kept forever") : L("History kept %lld days", Prefs.Long("keepDays")))
            + " · " + (Prefs.Str("pingHost").Length == 0 ? L("internet check off") : L("internet check on")));
        var foot = new Grid { Margin = new Thickness(2, 6, 2, 0), ColumnSpacing = 8 };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = Kit.CaptionText(L("You can change everything later in Settings (Ctrl+,)."));
        var share = new Button { Content = L("Share my settings…"), Padding = new Thickness(8, 3, 8, 3), FontSize = 12 };
        share.Click += async (_, _) => await Parts.ShareSettings(this);
        Grid.SetColumn(share, 1);
        foot.Children.Add(note); foot.Children.Add(share);
        BodyHost.Children.Add(foot);
    }
}
