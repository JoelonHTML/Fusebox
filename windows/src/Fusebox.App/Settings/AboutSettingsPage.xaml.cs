using Fusebox.Core.Update;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › About (FEATURES.md §9.16), like Windows' About: one card with icon, name, version and build, maker; updates (status as the
/// check row's description, a failure in an InfoBar), the beta performance report, and "What's new" from the win- release notes in a card
/// with the installed one marked.
/// </summary>
public sealed partial class AboutSettingsPage : Page
{
    readonly Button check = new();
    readonly InfoBar status = new() { IsClosable = false, IsOpen = false };
    readonly StackPanel changelog = new() { Spacing = 16 };
    readonly SettingsCard checkCard;
    List<ReleaseInfo>? shownLog;

    public AboutSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        // Like Windows' own About: the app's icon, name and version in one calm card, then the rows.
        var head = new Grid { ColumnSpacing = 20, Padding = new Thickness(0, 4, 0, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/Fusebox-256.png")), Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top });
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        names.Children.Add(new TextBlock { Text = "Fusebox", FontSize = 20, FontWeight = FontWeights.SemiBold });
        names.Children.Add(Quiet(L("Version %@", Updater.CurrentVersion)));
        names.Children.Add(Quiet(L("Made by") + " ClaudIA Software & Development"));
        var blurb = Quiet(L("A lightweight system monitor for your PC: live measurements, apps and advice."));
        blurb.Margin = new Thickness(0, 6, 0, 0);
        names.Children.Add(blurb);
        Grid.SetColumn(names, 1);
        head.Children.Add(names);
        Body.Children.Add(Block(head));

        Body.Children.Add(Section("Updates"));
        Body.Children.Add(Card("Update automatically", "Fusebox checks GitHub every 15 minutes, tells you when there is a new version, installs it and restarts. Your settings stay.", Toggle("autoUpdate"), ""));
        check.Content = L("Check for updates");
        check.Style = (Style)Application.Current.Resources["AccentButtonStyle"];   // the one primary action of this page
        check.Click += (_, _) => _ = Updater.Check(true);
        checkCard = Card("Check for updates", "", check, "");
        Body.Children.Add(checkCard);
        Body.Children.Add(status);

        Body.Children.Add(Section("Beta test"));
        var share = new Button { Content = L("Share performance report") };
        share.Click += async (_, _) =>
        {
            share.IsEnabled = false;
            share.Content = L("Measuring 5 s…");
            var file = await SelfUsage.ShareReport();
            share.Content = L("Share performance report");
            share.IsEnabled = true;
            if (file == null) Say(InfoBarSeverity.Error, L("Couldn't save the report."));
        };
        Body.Children.Add(Card("Beta: how light is Fusebox on this PC?", "Fusebox notes its own CPU and memory use once a minute, and looks for 5 seconds at what it is doing right now. This makes a small text file in Downloads (also copied) to send to the maker. Nothing is sent automatically.", share, ""));

        Body.Children.Add(Section("What's new"));
        Body.Children.Add(Block(changelog));

        Loaded += (_, _) => { Updater.Changed += OnUpdater; _ = Updater.LoadChangelog(); ShowUpdate(); };
        Unloaded += (_, _) => Updater.Changed -= OnUpdater;
    }

    void OnUpdater() => DispatcherQueue.TryEnqueue(ShowUpdate);

    void Say(InfoBarSeverity severity, string text)
    {
        status.Severity = severity;
        status.Message = text;
        status.IsOpen = text.Length > 0;
    }

    /// <summary>Update state as the check row's description (Windows Update style); only a failure gets the red bar.</summary>
    void ShowUpdate()
    {
        check.IsEnabled = Updater.Current is not (Updater.State.Checking or Updater.State.Installing);
        checkCard.SetDescriptionText(Updater.Current switch
        {
            Updater.State.Checking => L("Checking…"),
            Updater.State.UpToDate => L("You're up to date"),
            Updater.State.Available => L("Fusebox %@ available", UpdateLogic.Pretty(Updater.Tag ?? "")),
            Updater.State.Installing => L("Installing…"),
            Updater.State.Failed => "",
            _ => Updater.LastCheck is { } t ? L("Last checked %@", t.ToLocalTime().ToString("t", Culture)) : "",
        });
        Say(InfoBarSeverity.Error, Updater.Current == Updater.State.Failed ? Updater.Error ?? "" : "");
        if (!ReferenceEquals(Updater.Changelog, shownLog) || Updater.Changelog == null) ShowChangelog();
    }

    static TextBlock Quiet(string text) => new() { Text = text, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

    void ShowChangelog()
    {
        shownLog = Updater.Changelog;
        changelog.Children.Clear();
        var list = shownLog;
        if (list == null) { changelog.Children.Add(new TextBlock { Text = L("Loading…"), Opacity = 0.75 }); return; }
        if (list.Count == 0) { changelog.Children.Add(new TextBlock { Text = L("Couldn't load the changelog. Check your internet connection."), Opacity = 0.75, TextWrapping = TextWrapping.Wrap }); return; }
        foreach (var r in list)
        {
            var head = new Grid { ColumnSpacing = 6 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.Children.Add(new TextBlock { Text = r.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (new[] { r.Tag }.Concat(r.Builds).Any(t => UpdateLogic.BuildNumber(t) == Updater.CurrentBuild))
            {
                var badge = Badge(L("Installed"));
                Grid.SetColumn(badge, 1);
                head.Children.Add(badge);
            }
            if (r.Date is { } d)
            {
                var date = new TextBlock { Text = d.ToLocalTime().ToString("d MMMM yyyy", Culture), FontSize = 12, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(date, 2);
                head.Children.Add(date);
            }
            var item = new StackPanel { Spacing = 3 };
            item.Children.Add(head);
            foreach (var line in MiniMarkdown.Parse(r.Notes)) item.Children.Add(NoteLine(line));
            changelog.Children.Add(item);
        }
    }

    /// <summary>"Installed": caption, capsule filled with the accent at 15 %.</summary>
    static Border Badge(string text)
    {
        var accent = Kit.Accent();
        return new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(38, accent.R, accent.G, accent.B)),
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(Kit.AccentText()) },
        };
    }

    static TextBlock NoteLine(MdLine line)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = line.Heading ? 1 : 0.8, FontSize = 13 };
        if (line.Bullet) { tb.Margin = new Thickness(10, 0, 0, 0); tb.Inlines.Add(new Run { Text = "•  " }); }
        foreach (var s in line.Spans)
        {
            if (s.Url != null)
            {
                var link = new Hyperlink { NavigateUri = new Uri(s.Url) };
                link.Inlines.Add(new Run { Text = s.Text });
                tb.Inlines.Add(link);
            }
            else tb.Inlines.Add(new Run { Text = s.Text, FontWeight = s.Bold ? FontWeights.SemiBold : FontWeights.Normal });
        }
        return tb;
    }
}
