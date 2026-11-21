using Fusebox.Core.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › About (FEATURES.md §9.16). Has (from v1): version, update automatically, check for updates + status, changelog.
/// To do (Settings agent): header block with icon, credits, beta performance report.
/// </summary>
public sealed partial class AboutSettingsPage : Page
{
    readonly Button check = new();
    readonly SettingsCard updates;
    readonly StackPanel changelog = new() { Spacing = 12, Margin = new Thickness(2, 4, 2, 0) };

    public AboutSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
        var version = Card("Fusebox", "", null, "\uE946");
        version.SetDescriptionText(L("Version %@", Updater.CurrentVersion) + " · " + L("A lightweight system monitor for your PC: live measurements, apps and advice."));
        Body.Children.Add(version);
        Body.Children.Add(Section("Updates"));
        Body.Children.Add(Card("Update automatically", "", Toggle("autoUpdate"), "\uE895"));
        check.Content = L("Check for updates");
        check.Click += (_, _) => _ = Updater.Check(true);
        updates = Card("Check for updates", "", check, "\uE72C");
        Body.Children.Add(updates);
        Body.Children.Add(Section("What's new"));
        Body.Children.Add(changelog);
        Loaded += (_, _) => { Updater.Changed += OnUpdater; _ = Updater.LoadChangelog(); ShowUpdate(); };
        Unloaded += (_, _) => Updater.Changed -= OnUpdater;
    }

    void OnUpdater() => DispatcherQueue.TryEnqueue(ShowUpdate);

    void ShowUpdate()
    {
        check.IsEnabled = Updater.Current is not (Updater.State.Checking or Updater.State.Installing);
        updates.SetDescriptionText(Updater.Current switch
        {
            Updater.State.Checking => L("Checking…"),
            Updater.State.UpToDate => L("You're up to date"),
            Updater.State.Available => L("Fusebox %@ available", UpdateLogic.Pretty(Updater.Tag ?? "")),
            Updater.State.Installing => L("Installing…"),
            Updater.State.Failed => Updater.Error ?? "",
            _ => Updater.LastCheck is { } t ? L("Last checked %@", t.ToLocalTime().ToString("t", Culture)) : "",
        });
        changelog.Children.Clear();
        var list = Updater.Changelog;
        if (list == null) { changelog.Children.Add(new TextBlock { Text = L("Loading…"), Opacity = 0.7 }); return; }
        if (list.Count == 0) { changelog.Children.Add(new TextBlock { Text = L("Couldn't load the changelog. Check your internet connection."), Opacity = 0.7, TextWrapping = TextWrapping.Wrap }); return; }
        foreach (var r in list)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new TextBlock { Text = r.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (new[] { r.Tag }.Concat(r.Builds).Any(t => UpdateLogic.BuildNumber(t) == Updater.CurrentBuild))
                head.Children.Add(new TextBlock { Text = L("Installed"), Opacity = 0.7 });
            if (r.Date is { } d) head.Children.Add(new TextBlock { Text = d.ToLocalTime().ToString("d", Culture), Opacity = 0.7 });
            var item = new StackPanel { Spacing = 2 };
            item.Children.Add(head);
            if (r.Notes.Length > 0) item.Children.Add(new TextBlock { Text = r.Notes, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            changelog.Children.Add(item);
        }
    }
}
