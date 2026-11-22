using Fusebox.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Profile (FEATURES.md §9.4): four profile cards with what they change, Low impact (off / on / automatic, a drop-down like Windows),
/// share and import a settings file, run setup again, reset everything.
/// </summary>
public sealed partial class ProfileSettingsPage : Page
{
    readonly SettingsCard low;
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(2, 4, 2, 0) };

    public ProfileSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        Body.Children.Add(Section("Start with a profile (you can still change everything afterwards)"));
        Body.Children.Add(Block(Presets()));
        Body.Children.Add(status);

        low = Card("Low impact", "Fusebox measures every 5 seconds instead of 2, without glass effects, and Windows runs it in Efficiency mode. With the window open that is a small fraction of a processor core. Automatic: on in Energy saver, or on battery below 20%.",
            Choice("lowImpact", [("off", L("Off")), ("on", L("On")), ("auto", L("Automatic"))]), "\uE706");
        low.Margin = new Thickness(0, 20, 0, 0);
        Body.Children.Add(low);

        Body.Children.Add(Section("Share with friends"));
        var share = new Button { Content = L("Share my settings…") };
        share.Click += async (_, _) => await Parts.ShareSettings(SettingsWindow.Current);
        var import = new Button { Content = L("Import…") };
        import.Click += async (_, _) => { if (await Parts.ImportSettings(SettingsWindow.Current) is { } r) Say(r.Text); };
        var file = new SettingsCard { Header = "Fusebox-settings.json", Glyph = "\uE72D", Action = Kit.Row(8, share, import) };
        file.SetDescriptionText(L("Your ping server is not included."));
        Body.Children.Add(file);

        var setup = new Button { Content = L("Run setup again") };
        setup.Click += (_, _) => Boot.ShowSetup();
        var setupCard = Card("Run setup again", "", setup, "\uE713");
        setupCard.Margin = new Thickness(0, 24, 0, 0);
        Body.Children.Add(setupCard);

        // A normal button: the confirmation dialog is the safety, not a red label.
        var reset = new Button { Content = L("Reset everything…") };
        reset.Click += async (_, _) =>
        {
            if (!await Confirm(XamlRoot, L("Reset all settings to default?"), L("Your logs are kept."), L("Reset"))) return;
            Prefs.Store.ResetAll();
            Say(L("Everything is back to default"));
        };
        Body.Children.Add(Card("Reset everything…", "", reset, "\uE72C"));

        ShowLow();
        Loaded += (_, _) => LowImpact.Changed += OnLow;
        Unloaded += (_, _) => LowImpact.Changed -= OnLow;
    }

    void Say(string text) { status.Text = text; status.Visibility = Visibility.Visible; }

    /// <summary>Four profile tiles (same tile look as the theme tiles): icon, name, blurb, and what it changes in small text.</summary>
    UIElement Presets()
    {
        string[] glyphs = ["\uE706", "\uE7FC", "\uE756", "\uE7F8"];
        var tiles = new List<FrameworkElement>();
        for (int i = 0; i < Profile.All.Count; i++)
        {
            var p = Profile.All[i];
            string changes = SettingsText.Changes(p);
            var body = new StackPanel { Spacing = 4, Padding = new Thickness(2, 6, 2, 2) };
            body.Children.Add(new FontIcon { Glyph = glyphs[i], FontSize = 20, Height = 26 });
            body.Children.Add(new TextBlock { Text = L(p.Title), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center });
            body.Children.Add(new TextBlock { Text = L(p.Blurb), FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MinHeight = 32, Opacity = 0.75 });
            body.Children.Add(new TextBlock { Text = changes, FontSize = 11, Opacity = 0.6, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            var tile = new Tile(body, L(p.Title), () => { Prefs.Store.ApplyProfile(p.Id); Say(L("Profile %@ is on", L(p.Title))); });
            ToolTipService.SetToolTip(tile, L("Changes: %@", changes));
            tiles.Add(tile);
        }
        return Parts.Arrange(4, 200, tiles);
    }

    void OnLow() => DispatcherQueue.TryEnqueue(ShowLow);
    void ShowLow() => low.Header = LowImpact.On ? "Low impact · on now" : "Low impact";
}
