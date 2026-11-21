using System.Diagnostics;
using Fusebox.Core;
using Fusebox.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fusebox.App.Pages;

public sealed partial class SettingsPage : Page
{
    bool ready;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (ready) return;
            Loc.Apply(this);   // static XAML text first; everything below is set in code (already translated)
            Init();
            ready = true;
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Updater.Changed += OnUpdater;
        LowImpact.Changed += OnLow;
        _ = Updater.LoadChangelog();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Updater.Changed -= OnUpdater;
        LowImpact.Changed -= OnLow;
    }

    void OnUpdater() => DispatcherQueue.TryEnqueue(ShowUpdate);
    void OnLow() => DispatcherQueue.TryEnqueue(ShowLow);

    /// <summary>Fills a picker from (id, label) pairs and stores the choice under a Prefs key.</summary>
    static void Pick(ComboBox box, string key, string fallback, (string Id, string Label)[] items, Action? after = null)
    {
        var cur = Prefs.Get(key, fallback);
        foreach (var (_, label) in items) box.Items.Add(label);
        box.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.Id == cur));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            Prefs.Set(key, items[box.SelectedIndex].Id);
            after?.Invoke();
        };
    }

    static void Switch(ToggleSwitch t, string key, bool fallback, Action? after = null)
    {
        t.IsOn = Prefs.Get(key, fallback);
        t.Toggled += (_, _) => { Prefs.Set(key, t.IsOn); after?.Invoke(); };
    }

    void Init()
    {
        // Style / Mode / Font. Style, font and language names are never translated.
        Pick(ThemeBox, "theme", "glass", Theming.Styles.Select(s => (s.Id, s.Name)).ToArray());
        Pick(ModeBox, "appearanceMode", "system", [("system", Strings.L("System")), ("light", Strings.L("Light")), ("dark", Strings.L("Dark"))]);
        Pick(FontBox, "font", "style", [("style", Strings.L("Matches style")), ("segoe", "Segoe UI Variable"), ("cascadia", "Cascadia Mono"), ("montserrat", "Montserrat"), ("ubuntu", "Ubuntu")]);

        var startLanguage = Prefs.Str("appLanguage");
        Pick(LanguageBox, "appLanguage", "",
            [("", Strings.L("System")), ("en", "English"), ("nl", "Nederlands"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español")],
            () => RestartButton.Visibility = Prefs.Str("appLanguage") != startLanguage ? Visibility.Visible : Visibility.Collapsed);

        MetricsHost.Children.Add(MetricsEditor.Create());

        TrayToggle.IsOn = Prefs.Str("menubarStyle") != "off"; TrayToggle.Toggled += (_, _) => { if (TrayToggle.IsOn) Prefs.Reset("menubarStyle"); else Prefs.Set("menubarStyle", "off"); };
        LoginToggle.IsOn = StartAtLogin.Enabled;
        LoginToggle.Toggled += (_, _) => StartAtLogin.Set(LoginToggle.IsOn);
        HotkeyToggle.IsOn = Prefs.Int("hotkeyCode") > 0;
        HotkeyToggle.Toggled += (_, _) =>
        {
            if (HotkeyToggle.IsOn) Prefs.Reset("hotkeyCode"); else Prefs.Set("hotkeyCode", -1);
            bool taken = HotkeyToggle.IsOn && !Hotkey.Registered;
            HotkeyNote.Text = taken ? Strings.L("Another app already uses this shortcut.") : "";
            HotkeyNote.Visibility = taken ? Visibility.Visible : Visibility.Collapsed;
        };

        Switch(NotifyToggle, "notify", true, () => TestButton.IsEnabled = NotifyToggle.IsOn);
        TestButton.IsEnabled = NotifyToggle.IsOn;

        Pick(LowBox, "lowImpact", "auto", [("off", Strings.L("Off")), ("on", Strings.L("On")), ("auto", Strings.L("Automatic"))]);
        ShowLow();

        FolderText.Text = Strings.L("Folder") + ": " + LogDir();
        var host = Prefs.Get("pingHost", "1.1.1.1");
        PingToggle.IsOn = host.Length > 0;
        PingHost.Text = host;
        PingHost.Visibility = host.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowPingNote();
        PingToggle.Toggled += (_, _) =>
        {
            Prefs.Set("pingHost", PingToggle.IsOn ? "1.1.1.1" : "");
            PingHost.Text = PingToggle.IsOn ? "1.1.1.1" : "";
            PingHost.Visibility = PingToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            ShowPingNote();
        };
        PingHost.LostFocus += (_, _) =>
        {
            var h = PingHost.Text.Trim();
            if (h.Length > 0) Prefs.Set("pingHost", h);
        };

        VersionText.Text = Strings.L("Version %@", Updater.CurrentVersion);
        Switch(AutoToggle, "autoUpdate", true);
        ShowUpdate();
    }

    void ShowPingNote() => PingNote.Text = PingToggle.IsOn
        ? Strings.L("One short connection to this server every 10 s, port 443. Nothing else goes to the internet.")
        : Strings.L("Off: Fusebox makes no internet connections at all.");

    void ShowLow() => LowTitle.Text = LowImpact.On ? Strings.L("Low impact · on now") : Strings.L("Low impact");

    void ShowUpdate()
    {
        var busy = Updater.Current is Updater.State.Checking or Updater.State.Installing;
        CheckButton.IsEnabled = !busy;
        UpdateStatus.Text = Updater.Current switch
        {
            Updater.State.Checking => Strings.L("Checking…"),
            Updater.State.UpToDate => Strings.L("You're up to date"),
            Updater.State.Available => Strings.L("Fusebox %@ available", UpdateLogic.Pretty(Updater.Tag ?? "")),
            Updater.State.Installing => Strings.L("Installing…"),
            Updater.State.Failed => Updater.Error ?? "",
            _ => Updater.LastCheck is { } t ? Strings.L("Last checked %@", t.ToLocalTime().ToString("t", Strings.Culture)) : "",
        };
        ShowChangelog();
    }

    void ShowChangelog()
    {
        ChangelogHost.Children.Clear();
        var list = Updater.Changelog;
        if (list == null) { ChangelogHost.Children.Add(new TextBlock { Text = Strings.L("Loading…"), Opacity = 0.7 }); return; }
        if (list.Count == 0) { ChangelogHost.Children.Add(new TextBlock { Text = Strings.L("Couldn't load the changelog. Check your internet connection."), Opacity = 0.7, TextWrapping = TextWrapping.Wrap }); return; }
        foreach (var r in list)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = r.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (new[] { r.Tag }.Concat(r.Builds).Any(t => UpdateLogic.BuildNumber(t) == Updater.CurrentBuild))
                head.Children.Add(new TextBlock { Text = Strings.L("Installed"), Opacity = 0.7 });
            if (r.Date is { } d) head.Children.Add(new TextBlock { Text = d.ToLocalTime().ToString("d", Strings.Culture), Opacity = 0.7 });
            var item = new StackPanel { Spacing = 2 };
            item.Children.Add(head);
            if (r.Notes.Length > 0) item.Children.Add(new TextBlock { Text = r.Notes, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            ChangelogHost.Children.Add(item);
        }
    }

    static string LogDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fusebox");

    void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogDir());
            Process.Start(new ProcessStartInfo(LogDir()) { UseShellExecute = true });
        }
        catch { }
    }

    void Test_Click(object sender, RoutedEventArgs e) =>
        Notify.Send(Strings.L("Fusebox update available (test)"), Strings.L("This is how Fusebox tells you about a new version."));

    void Check_Click(object sender, RoutedEventArgs e) => _ = Updater.Check(true);

    void Setup_Click(object sender, RoutedEventArgs e) => Boot.ShowSetup();

    void Restart_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false });
        Application.Current.Exit();
    }
}
