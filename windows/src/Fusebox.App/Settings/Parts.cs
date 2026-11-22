using Fusebox.Core;
using Fusebox.Core.Alerts;
using Fusebox.Core.Look;
using Fusebox.Core.Settings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>Pieces used by both the Settings pages and the setup window, so a choice looks and works the same in both.</summary>
internal static class Parts
{
    // ---- tile grids ----

    /// <summary>
    /// <paramref name="child"/> stretched up to <paramref name="maxWidth"/>, then left-aligned (a star column with a maximum): tiles and previews
    /// never grow huge in a wide window and shrink in a narrow one.
    /// </summary>
    public static Grid Capped(double maxWidth, UIElement child)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = maxWidth });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(child);
        return g;
    }

    /// <summary>Tiles in an aligned grid: <paramref name="columns"/> equal columns, each at most <paramref name="tileWidth"/> wide, 8 apart.</summary>
    public static Grid Arrange(int columns, double tileWidth, IReadOnlyList<FrameworkElement> tiles)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < (tiles.Count + columns - 1) / columns; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < tiles.Count; i++)
        {
            tiles[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            tiles[i].VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(tiles[i], i % columns);
            Grid.SetRow(tiles[i], i / columns);
            grid.Children.Add(tiles[i]);
        }
        return Capped(columns * tileWidth + (columns - 1) * 8, grid);
    }

    /// <summary>
    /// Grid of selectable tiles (<see cref="Arrange"/>). <paramref name="isSelected"/> marks the stored choice, <paramref name="pick"/> stores one.
    /// Previews are rebuilt when the Fusebox theme changes (accent, mode, font …), the selection when <paramref name="key"/> changes.
    /// </summary>
    public static Grid TileGrid(int columns, double tileWidth, (string Id, string Name, Func<UIElement> Content)[] items, string key, Func<string, bool> isSelected,
        Action<string> pick, bool rebuildOnTheme)
    {
        var tiles = items.Select(i => (i.Id, Tile: new Tile(i.Content(), i.Name, () => pick(i.Id)), i.Content)).ToList();
        var grid = Arrange(columns, tileWidth, tiles.Select(t => (FrameworkElement)t.Tile).ToList());
        void Select() { foreach (var t in tiles) t.Tile.Selected = isSelected(t.Id); }
        Select();
        Kit.WhilePrefs(grid, k => { if (k == key) Select(); });
        if (rebuildOnTheme) Kit.WhileTheme(grid, () => { foreach (var t in tiles) t.Tile.Child = t.Content(); Select(); });
        return grid;
    }

    static StackPanel Stack(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Spacing = spacing };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    static TextBlock Name(string text, double size = 13, int maxLines = 2) =>
        new() { Text = text, FontSize = size, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = maxLines, HorizontalAlignment = HorizontalAlignment.Center };

    // ---- appearance ----

    /// <summary>Six theme tiles (3 columns), each a mini Live window in that theme (§11.7). Theme names are never translated.</summary>
    public static UIElement ThemeTiles() => TileGrid(3, 196,
        Themes.All.Select(t => (t.Id, t.Name, (Func<UIElement>)(() => Stack(6, Previews.ThemeTile(Previews.Resolve(t.Id, 1)), Name(t.Name))))).ToArray(),
        "theme", id => Prefs.Str("theme") == id, id => Prefs.Set("theme", id), true);

    /// <summary>Normal / Large / Extra large cards and a sample row in the current theme (§11.7).</summary>
    public static UIElement TextSizeCards()
    {
        (string, string, double)[] sizes = [("normal", L("Normal"), 1.0), ("large", L("Large"), 1.15), ("xl", L("Extra large"), 1.3)];
        var cards = TileGrid(3, 150,
            sizes.Select(s => (s.Item1, s.Item2, (Func<UIElement>)(() => Stack(4,
                new TextBlock { Text = "Aa", FontSize = 22 * s.Item3, FontWeight = FontWeights.SemiBold, Height = 36 * 1.3, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                Name(s.Item2, 12))))).ToArray(),
            "textScale", id => Math.Abs(Prefs.Double("textScale") - sizes.First(s => s.Item1 == id).Item3) < 0.01,
            id => Prefs.Set("textScale", sizes.First(s => s.Item1 == id).Item3), false);
        var sample = new ContentPresenter { Content = Previews.TextSample(), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Kit.WhileTheme(sample, () => sample.Content = Previews.TextSample());
        return Capped(3 * 150 + 16, Stack(8, cards, sample));
    }

    /// <summary>Seven font cards (4 columns): "17%" in that font with the theme's weight, "CPU 47°", the font name.</summary>
    public static UIElement FontCards()
    {
        (string Id, string Name)[] fonts =
        [
            ("style", L("Matches style")), ("sfPro", "Segoe UI Variable"), ("rounded", "Nunito"), ("mono", "Cascadia Mono"),
            ("serif", "Georgia"), ("montserrat", "Montserrat"), ("ubuntu", "Ubuntu"),
        ];
        UIElement Card(string id, string name)
        {
            var face = ThemeService.Family((ThemeService.Current with { Font = id }).Face);
            var w = ThemeService.Current.Weight switch { NumberWeight.Bold => FontWeights.Bold, NumberWeight.Semibold => FontWeights.SemiBold, _ => FontWeights.Normal };
            var big = new TextBlock { Text = "17%", FontFamily = face, FontSize = 22, FontWeight = w, Height = 30, HorizontalAlignment = HorizontalAlignment.Center };
            var small = new TextBlock { Text = "CPU 47°", FontFamily = face, FontSize = 11, FontWeight = FontWeights.Medium, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Center };
            return Stack(2, big, small, new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center });
        }
        return TileGrid(4, 140, fonts.Select(f => (f.Id, f.Name, (Func<UIElement>)(() => Card(f.Id, f.Name)))).ToArray(),
            "font", id => Prefs.Str("font") == id, id => Prefs.Set("font", id), true);
    }

    /// <summary>Accent: a swatch that opens a color picker, a sample capsule, and "Default" (§9.5).</summary>
    public static UIElement AccentPicker()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var swatch = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12) };
        var picker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false, IsHexInputVisible = true };
        var button = new Button { Content = swatch, Padding = new Thickness(6), Flyout = new Flyout { Content = picker } };
        var sample = new ContentPresenter { Content = Previews.AccentSample() };
        var reset = new Button { Content = L("Default") };
        bool setting = false;
        void Show()
        {
            var accent = Argb.FromHex(Prefs.Str("accent")) ?? ThemeService.Current.Accent;
            swatch.Background = Previews.B(accent);
            setting = true;
            picker.Color = ThemeService.C(accent);
            setting = false;
            sample.Content = Previews.AccentSample();
            reset.IsEnabled = Prefs.Str("accent").Length > 0;
        }
        picker.ColorChanged += (_, e) =>
        {
            if (setting) return;
            Prefs.Set("accent", new Argb(255, e.NewColor.R, e.NewColor.G, e.NewColor.B).Hex);
        };
        reset.Click += (_, _) => Prefs.Reset("accent");
        row.Children.Add(button); row.Children.Add(sample); row.Children.Add(reset);
        Show();
        Kit.WhileTheme(row, Show);
        return row;
    }

    // ---- live page ----

    /// <summary>Automatic / Never (§9.6): graphs on the larger window.</summary>
    public static UIElement GraphCards()
    {
        UIElement Card(string glyphOrText, bool text, string title, string detail)
        {
            UIElement icon = text ? new TextBlock { Text = glyphOrText, FontSize = 18, FontWeight = FontWeights.SemiBold, Height = 28, HorizontalAlignment = HorizontalAlignment.Center }
                                  : new FontIcon { Glyph = glyphOrText, FontSize = 20, Height = 28, HorizontalAlignment = HorizontalAlignment.Center };
            return Stack(5, icon, new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = detail, FontSize = 12, Opacity = 0.75, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MinHeight = 32 });
        }
        return TileGrid(2, 240,
        [
            ("auto", L("Automatic"), () => Card("", false, L("Automatic"), L("Graphs appear when the window is big enough"))),
            ("never", L("Never"), () => Card("123", true, L("Never"), L("Always just the numbers"))),
        ], "graphs", id => (Prefs.Str("graphs") == "never") == (id == "never"), id => Prefs.Set("graphs", id), false);
    }

    /// <summary>One "% / cores · GB" row for CPU, RAM or SSD with what each shows on this PC (§9.6).</summary>
    public static SettingsCard UnitPicker(Metric m)
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        double gb = App.Live.Last is { RamTotal: > 0 } s ? s.RamTotal / 1e9 : 16;
        string unit = m == Metric.Cpu ? L("cores") : "GB";
        string abs = m switch { Metric.Cpu => $"{(0.23 * cores).ToString("0.0", Culture)} /{cores}", Metric.Ram => $"{(0.61 * gb).ToString("0.0", Culture)} GB", _ => "300 GB" };
        string pct = m switch { Metric.Cpu => "23%", Metric.Ram => "61%", _ => "40%" };
        var card = new SettingsCard { Header = Metrics.Title(m) };
        card.SetDescriptionText(L("%% shows %@ · %@ shows %@", pct, unit, abs));
        string id = Metrics.Id(m);
        card.Action = Kit.Pick([("pct", "%"), ("abs", unit)], Prefs.List("absolute").Contains(id) ? "abs" : "pct", v =>
        {
            var l = Prefs.List("absolute").Where(x => x != id).ToList();
            if (v == "abs") l.Add(id);
            Prefs.SetList("absolute", l);
        });
        return card;
    }

    // ---- tray ----

    public static readonly (string Id, string Title)[] TrayStyles =
    [
        ("text", "Text (CPU 23%)"), ("compact", "Compact (small symbols)"), ("bars", "Bars"), ("graph", "Graphs"), ("icon", "Just an icon"), ("off", "Don't show"),
    ];

    /// <summary>Six tray style cards (3 columns) with a preview of each; "Don't show" is disabled while the taskbar button is off (§9.11).</summary>
    public static UIElement TrayStyleCards()
    {
        string TitleOf(string id) => id switch
        {
            "text" => L("Text (CPU 23%)"), "compact" => L("Compact (small symbols)"), "bars" => L("Bars"), "graph" => L("Graphs"), "icon" => L("Just an icon"), _ => L("Don't show"),
        };
        var grid = TileGrid(3, 170, TrayStyles.Select(s => (s.Id, TitleOf(s.Id), (Func<UIElement>)(() =>
            Stack(6, Previews.TrayPreview(s.Id, Metrics.Parse(Prefs.Str("menubarMetrics"))), new TextBlock { Text = TitleOf(s.Id), FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MinHeight = 32 })))).ToArray(),
            "menubarStyle", id => Prefs.Str("menubarStyle") == id, id => Prefs.Set("menubarStyle", id), true);
        void Reach()
        {
            foreach (var t in ((Grid)grid.Children[0]).Children.OfType<Tile>().Skip(5)) t.SetEnabled(Prefs.Bool("dockIcon"));
        }
        Reach();
        Kit.WhilePrefs(grid, k => { if (k is "dockIcon" or "menubarMetrics") Reach(); });
        return grid;
    }

    // ---- alerts ----

    /// <summary>
    /// One alert kind (§9.12): an expandable row (switch, limit in words) whose sub-options are the limit, the minutes in a row and an example
    /// notification. <paramref name="compact"/> (setup): just the row with its switch.
    /// </summary>
    public static UIElement AlertRow(string id, bool compact = false)
    {
        string on = AlertKinds.Key(id, "on"), limit = AlertKinds.Key(id, "limit"), min = AlertKinds.Key(id, "min");
        var (lo, hi, step) = AlertKinds.Range(id);
        var card = new SettingsCard { Header = AlertKinds.Title(id) };
        var toggle = new ToggleSwitch { IsOn = Prefs.Bool(on), MinWidth = 0 };
        card.Action = toggle;
        toggle.Toggled += (_, _) => Prefs.Set(on, toggle.IsOn);
        var limitRow = SettingsUi.Item(new SettingsCard());
        var toast = new ToastMock("", "");
        void Show()
        {
            double l = Prefs.Double(limit); int m = (int)Prefs.Long(min);
            card.SetDescriptionText(L("%@, %lld min in a row", AlertKinds.Describe(id, l), m));
            limitRow.SetHeaderText(L("Limit: %@", AlertKinds.Describe(id, l)));
            toast.Set(AlertKinds.Title(id), AlertKinds.Example(id, l, m));
        }
        Show();
        if (compact) return card;
        limitRow.Action = Kit.Stepper(lo, hi, step, Prefs.Double(limit), v => { Prefs.Set(limit, v); Show(); }, 124);
        var minRow = SettingsUi.Item(Card("Minutes in a row", "", Kit.Stepper(1, 60, 1, Prefs.Long(min), v => { Prefs.Set(min, (long)v); Show(); }, 124)));
        return SettingsUi.Expand(card, limitRow, minRow, Example(toast));
    }

    /// <summary>"Sustained peak" (§9.12): its own switch and minutes, with an example of the peak notification.</summary>
    public static UIElement PeakRow()
    {
        var card = new SettingsCard { Header = "Sustained peak" };
        var toggle = new ToggleSwitch { IsOn = Prefs.Bool("alert.peak.on"), MinWidth = 0 };
        card.Action = toggle;
        toggle.Toggled += (_, _) => Prefs.Set("alert.peak.on", toggle.IsOn);
        var toast = new ToastMock("", "");
        void Show()
        {
            int m = Math.Max(1, (int)Prefs.Long("alert.peak.min"));
            card.SetDescriptionText(L("Names the app behind a peak of %lld min or more, with a Stop app button", m));
            string body = m == 1 ? L("%@ %@ for 1 minute", "CPU", "92%") : L("%@ %@ for %lld minutes", "CPU", "92%", m);
            toast.Set(L("%@ is keeping your CPU busy", "Safari"), body);
        }
        var minRow = SettingsUi.Item(Card("Minutes in a row", "", Kit.Stepper(1, 30, 1, Prefs.Long("alert.peak.min"), v => { Prefs.Set("alert.peak.min", (long)v); Show(); }, 124)));
        Show();
        return SettingsUi.Expand(card, minRow, Example(toast));
    }

    /// <summary>The example notification under an alert's options: not wider than a real one, with room below.</summary>
    static UIElement Example(ToastMock toast)
    {
        toast.MaxWidth = 364;
        toast.HorizontalAlignment = HorizontalAlignment.Left;
        toast.Margin = new Thickness(0, 4, 0, 10);
        return toast;
    }

    /// <summary>Alert kinds this PC can show: "PC too hot" only when a temperature is measured.</summary>
    public static IEnumerable<string> AlertIds() => AlertKinds.Ids.Where(id => id != "heat" || double.IsFinite(App.Live.Last?.TempC ?? double.NaN));

    public static bool HasBattery => double.IsFinite(App.Live.Last?.Battery ?? double.NaN) || SettingsWindow.HasBattery();

    // ---- files (share / import) ----

    /// <summary>"Share my settings…": save Fusebox-settings.json (Shared prefs only) through the Windows save dialog. Null = cancelled.</summary>
    public static async Task<bool> ShareSettings(Window? owner)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "Fusebox-settings", SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return false;
            await Windows.Storage.FileIO.WriteTextAsync(file, Prefs.Store.Export());
            return true;
        }
        catch { return false; }
    }

    /// <summary>"Import…": open a settings file. Text = "12 settings imported" / "Fusebox can't read this file."; null when cancelled.</summary>
    public static async Task<(string Text, bool Ok)?> ImportSettings(Window? owner)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return null;
            return Prefs.Store.ImportFile(file.Path) is { } n ? (L("%lld settings imported", n), true) : (L("Fusebox can't read this file."), false);
        }
        catch { return (L("Fusebox can't read this file."), false); }
    }

    /// <summary>The taskbar button switch. It stays on while the tray icon is off: Fusebox would be unreachable.</summary>
    public static SettingsCard TaskbarCard()
    {
        var card = Card("Show in the taskbar", "", null, "\uE7F4");
        var toggle = new ToggleSwitch { MinWidth = 0 };
        void Show()
        {
            bool trayOff = Prefs.Str("menubarStyle") == "off";
            toggle.IsOn = Prefs.Bool("dockIcon");
            toggle.IsEnabled = !trayOff;
            card.SetDescriptionText(trayOff ? L("Turn on the tray icon first, otherwise you can't find Fusebox anymore")
                                            : L("Without the taskbar button, you'll find Fusebox in the system tray."));
        }
        toggle.Toggled += (_, _) => { if (toggle.IsEnabled) Prefs.Set("dockIcon", toggle.IsOn); };
        card.Action = toggle;
        Show();
        Kit.WhilePrefs(card, k => { if (k is "dockIcon" or "menubarStyle") Show(); });
        return card;
    }
}
