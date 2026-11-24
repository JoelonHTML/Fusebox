using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Live page (FEATURES.md §9.6): a live preview, the measurement list (drag to reorder), graphs, small-window options with
/// previews, units and thresholds. Rows about things this PC doesn't have (battery, temperatures) are left out.
/// </summary>
public sealed partial class LiveSettingsPage : Page
{
    readonly List<SettingsCard> graphOnly = [];

    public LiveSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
        bool temps = double.IsFinite(App.Live.Last?.TempC ?? double.NaN) || double.IsFinite(App.Live.Last?.GpuTempC ?? double.NaN);

        Body.Children.Add(Section("Preview"));
        var preview = new ContentPresenter { Content = Previews.LivePreview() };
        void Redraw() => preview.Content = Previews.LivePreview();
        Kit.WhileTheme(preview, Redraw);
        Kit.WhilePrefs(preview, k => { if (k is "metrics" or "graphs" or "shortMinutes" or "showAdvice" or "absolute" or "netBits" or "appearanceMode") Redraw(); });
        var previewNote = new ContentPresenter { Margin = new Thickness(0, 6, 0, 0) };
        void Note() => previewNote.Content = Kit.CaptionText(Prefs.Str("graphs") == "never" ? L("Graphs are off: just the numbers.") : L("Graphs only appear when the window is big enough."));
        Note();
        Kit.WhilePrefs(previewNote, k => { if (k == "graphs") Note(); });
        Body.Children.Add(Block(Parts.Capped(640, Stack(preview, previewNote))));

        Body.Children.Add(Section("Measurements (drag to change the order)"));
        Body.Children.Add(Block(MetricsEditor.Create("metrics")));

        Body.Children.Add(Section("Larger window: graphs"));
        Body.Children.Add(Block(Parts.GraphCards()));
        AddG(Card("Graphs show the last", "", MinutesBox()));
        if (temps)
        {
            AddG(Card("CPU graph", "", Choice("cpuGraph", [("usage", L("Usage")), ("temp", L("Temperature")), ("both", L("Both"))])));
            AddG(Card("GPU graph", "", Choice("gpuGraph", [("usage", L("Usage")), ("temp", L("Temperature")), ("both", L("Both"))])));
        }
        AddG(Card("Smooth large graphs", "Large graphs glide instead of stepping with each measurement. Costs a little more processor in a big window.", Toggle("smoothBig")));
        void Graphs() { foreach (var c in graphOnly) c.IsEnabled = Prefs.Str("graphs") != "never"; }
        Graphs();
        Kit.WhilePrefs(Body, k => { if (k == "graphs") Graphs(); });

        Body.Children.Add(Section("Small window"));
        var coreRow = new ContentPresenter();
        void Cores() => coreRow.Content = Previews.OptionPreview("CPU", null, temps ? "57°" : "", "23%", "", Prefs.Bool("perCore") ? CoreBars() : null);
        Cores();
        Kit.WhileTheme(coreRow, Cores);
        Kit.WhilePrefs(coreRow, k => { if (k == "perCore") Cores(); });
        Body.Children.Add(WithPreview(Card("Per-core CPU", "A thin bar per processor core. Large windows always show them.", Toggle("perCore")), coreRow));

        Body.Children.Add(Card("Show advice line (\"Your PC is busy\", \"Memory is filling up\" …)", "", Toggle("showAdvice")));
        Body.Children.Add(Card("Response time next to NET", "How quickly the internet answers, in milliseconds (ms). Lower is better.", Toggle("netPing")));
        var net = new ContentPresenter();
        void Net() => net.Content = Previews.OptionPreview("NET", "", string.Join(" · ", new[] { Prefs.Bool("netPing") ? "17 ms" : null, Prefs.Bool("netSpeed") ? "412 Mbit" : null }.OfType<string>()), "↓8 ↑2", "KB/s");
        Net();
        Kit.WhileTheme(net, Net);
        Kit.WhilePrefs(net, k => { if (k is "netPing" or "netSpeed") Net(); });
        Body.Children.Add(WithPreview(Card("Internet speed next to NET", "Your download speed from your last speed test (Network tab). Empty until you run one.", Toggle("netSpeed")), net));

        if (Parts.HasBattery)
        {
            var bat = new ContentPresenter();
            void Bat() => bat.Content = Previews.OptionPreview("BAT", "", Prefs.Bool("batteryTime") ? "2:35" : "", "81%", "");
            Bat();
            Kit.WhileTheme(bat, Bat);
            Kit.WhilePrefs(bat, k => { if (k == "batteryTime") Bat(); });
            Body.Children.Add(WithPreview(Card("Battery time", "Next to BAT: on battery, how long until empty; while charging (battery with a bolt), how long until full. Windows' own estimate.", Toggle("batteryTime")), bat));
            Body.Children.Add(Card("Learn battery time from my use", "On battery, \"time left\" comes from how fast your battery drained over the last 7 days, mixed with the last 15 minutes. Needs 2 hours on battery first; until then Windows' estimate.", Toggle("learnedBattery")));
        }

        Body.Children.Add(Section("Units"));
        foreach (var m in new[] { Metric.Cpu, Metric.Ram, Metric.Ssd }) Body.Children.Add(Parts.UnitPicker(m));
        Body.Children.Add(Card("Internet (NET)", "", ChoiceBool("netBits", "MB/s", "Mbit/s")));

        Body.Children.Add(Section("Thresholds: orange from here, red 10 higher"));
        Body.Children.Add(Threshold("Processor", "warnCPU", 50, 100, 5, "%"));
        if (temps) Body.Children.Add(Threshold("Temperature", "warnTemp", 70, 110, 5, "°C"));
        Body.Children.Add(Threshold("Latency", "warnPing", 20, 300, 10, "ms"));
    }

    /// <summary>A row with its live sample inside the same card, under the text (not wider than a small Fusebox window).</summary>
    static SettingsCard WithPreview(SettingsCard card, UIElement preview)
    {
        card.Below = Parts.Capped(420, preview);
        return card;
    }

    static StackPanel Stack(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    /// <summary>A card that is greyed out while graphs are "never".</summary>
    void AddG(SettingsCard c) { Body.Children.Add(c); graphOnly.Add(c); }

    /// <summary>"Graphs show the last" 1 / 5 / 10 / 15 / 30 min / hour (the pref is a number).</summary>
    static ComboBox MinutesBox()
    {
        int[] v = [1, 5, 10, 15, 30, 60];
        var box = new ComboBox { MinWidth = 180 };
        foreach (var m in v) box.Items.Add(m == 60 ? L("hour") : L("%lld min", m));
        box.SelectedIndex = Math.Max(0, Array.IndexOf(v, (int)Prefs.Long("shortMinutes")));
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) Prefs.Set("shortMinutes", (long)v[box.SelectedIndex]); };
        return box;
    }

    /// <summary>A slider with its value ("85 %") on the right.</summary>
    static SettingsCard Threshold(string title, string key, double min, double max, double step, string unit)
    {
        var value = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(value, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Clamp(Prefs.Double(key), min, max), Width = 200, VerticalAlignment = VerticalAlignment.Center };
        void Show() => value.Text = $"{(int)slider.Value} {unit}";
        slider.ValueChanged += (_, _) => { Prefs.Set(key, slider.Value); Show(); };
        Show();
        return new SettingsCard { Header = title, Action = Kit.Row(8, slider, value) };
    }

    /// <summary>Ten thin bars in two groups (performance / efficiency cores), like the Live card's strip.</summary>
    static UIElement CoreBars()
    {
        double[] v = [70, 55, 40, 30, 20, 15, 60, 45, 35, 25];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };
        var color = Previews.B(Previews.Current().Graph);
        for (int i = 0; i < v.Length; i++)
        {
            if (i == 6) row.Children.Add(new Rectangle { Width = 3, Height = 1 });
            row.Children.Add(new Rectangle { Width = 4, Height = 14 * v[i] / 100, Fill = color, VerticalAlignment = VerticalAlignment.Bottom });
        }
        return row;
    }
}
