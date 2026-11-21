using Fusebox.Core;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Pages;

/// <summary>
/// The expanded details of a row and the split-mode pane (§4.9): buttons first (Details, Show in Explorer, Stop) so they don't jump,
/// then Efficiency mode (Windows extra), the explanation lines and the coloured verdict.
/// </summary>
sealed partial class ProcDetails
{
    public readonly StackPanel Root = new() { Spacing = 5 };
    readonly Button reveal, stop;
    readonly StackPanel linesPanel = new() { Spacing = 5 };
    readonly Grid verdict = new() { ColumnSpacing = 6 };
    readonly FontIcon verdictIcon = ProcUi.Glyph("", 12, "FuseboxGood");
    readonly TextBlock verdictText = ProcUi.Text(11, "FuseboxGood");
    readonly ToggleSwitch eco = new() { MinWidth = 0, OnContent = "", OffContent = "", Margin = new Thickness(0, 0, 0, 0) };
    readonly StackPanel ecoRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly TextBlock stopText;
    readonly FontIcon stopIcon;
    string? ecoKey;
    int ecoTicket;
    bool suppress;
    AppGroup? group;

    public event Action<AppGroup>? DetailsClicked, StopClicked;

    /// <summary>Left indent: 26 under the name in an expanded row, 0 in the split pane.</summary>
    public ProcDetails(double indent)
    {
        Root.Padding = new Thickness(indent, 6, 0, 2);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var details = ProcUi.IconButton("", Strings.L("Details"), "FuseboxOnRowSecondary");
        details.Click += (_, _) => { if (group != null) DetailsClicked?.Invoke(group); };
        reveal = ProcUi.IconButton("", Strings.L("Show in Explorer"), "FuseboxOnRowSecondary");
        reveal.Click += (_, _) => { if (group?.ExePath is { } p) ProcessMonitor.Reveal(p); };
        stop = ProcUi.IconButton("", Strings.L("Stop"), "FuseboxDanger");
        stop.Click += (_, _) => { if (group != null) StopClicked?.Invoke(group); };
        stopText = (TextBlock)((StackPanel)stop.Content).Children[1];
        stopIcon = (FontIcon)((StackPanel)stop.Content).Children[0];
        buttons.Children.Add(details);
        buttons.Children.Add(reveal);
        buttons.Children.Add(stop);
        Root.Children.Add(buttons);

        // Windows extra: Efficiency mode (EcoQoS), a gentler alternative to Stop.
        var ecoLabel = ProcUi.Text(11, "FuseboxOnRowSecondary");
        ecoLabel.Text = Strings.L("Efficiency mode");
        ecoLabel.VerticalAlignment = VerticalAlignment.Center;
        ecoRow.Children.Add(eco);
        ecoRow.Children.Add(ecoLabel);
        string ecoTip = Strings.L("Keeps the app open, but lets Windows run it with less power. Like Efficiency mode in Task Manager.");
        ToolTipService.SetToolTip(ecoRow, ecoTip);
        AutomationProperties.SetName(eco, Strings.L("Efficiency mode"));
        AutomationProperties.SetHelpText(eco, ecoTip);
        eco.Toggled += Eco_Toggled;
        Root.Children.Add(ecoRow);

        Root.Children.Add(linesPanel);

        verdict.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        verdict.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        verdictIcon.HorizontalAlignment = HorizontalAlignment.Left;
        verdict.Children.Add(verdictIcon);
        Grid.SetColumn(verdictText, 1);
        verdictText.TextWrapping = TextWrapping.Wrap;
        verdict.Children.Add(verdictText);
        Root.Children.Add(verdict);
    }

    public void Show(AppGroup g, int cpus, double ramTotal, DateTime now)
    {
        group = g;
        reveal.Visibility = g.ExePath != null ? Visibility.Visible : Visibility.Collapsed;
        bool canStop = g.CanStop;
        stop.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
        string stopLabel = Strings.L("Stop %@", ProcessText.NameOf(g));
        ProcUi.Set(stopText, stopLabel);
        ToolTipService.SetToolTip(stop, stopLabel);
        AutomationProperties.SetName(stop, stopLabel);
        var blocks = ThemeService.Current.RowStyle == Core.Look.RowStyle.Blocks;
        stopIcon.Foreground = stopText.Foreground = ThemeService.Brush(blocks ? "FuseboxOnRow" : "FuseboxDanger");

        // Efficiency mode: only for apps we may touch (not Windows itself, not Fusebox).
        bool ecoOk = canStop && g.Kind != AppKind.Windows && g.Pids.Count > 0;
        ecoRow.Visibility = ecoOk ? Visibility.Visible : Visibility.Collapsed;
        if (ecoOk && ecoKey != g.Key) { ecoKey = g.Key; _ = QueryEco(g); }

        var lines = ProcessText.LinesWithIcons(g, cpus, ramTotal, now);
        while (linesPanel.Children.Count > lines.Count) linesPanel.Children.RemoveAt(linesPanel.Children.Count - 1);
        while (linesPanel.Children.Count < lines.Count)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = ProcUi.Glyph("", 12, "FuseboxOnRowSecondary");
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 0, 0);
            var text = ProcUi.Text(11, "FuseboxOnRowSecondary");
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            Grid.SetColumn(text, 1);
            row.Children.Add(icon);
            row.Children.Add(text);
            linesPanel.Children.Add(row);
        }
        for (int i = 0; i < lines.Count; i++)
        {
            var row = (Grid)linesPanel.Children[i];
            var icon = (FontIcon)row.Children[0];
            if (icon.Glyph != lines[i].Glyph) icon.Glyph = lines[i].Glyph;
            ProcUi.Set((TextBlock)row.Children[1], lines[i].Text);
        }

        var v = ProcessText.Judge(g, ramTotal);
        var brush = ProcUi.VerdictBrush(v);
        verdictIcon.Foreground = verdictText.Foreground = brush;
        string glyph = v == ProcessText.Verdict.Normal ? "" : v == ProcessText.Verdict.HighMemory ? "" : "";
        if (verdictIcon.Glyph != glyph) verdictIcon.Glyph = glyph;
        ProcUi.Set(verdictText, ProcessText.VerdictText(v));
    }

    async Task QueryEco(AppGroup g)
    {
        int ticket = ++ecoTicket;
        var pids = g.Pids.ToList();
        bool on = await Task.Run(() => ProcessControl.IsEfficient(pids));
        if (ticket != ecoTicket) return;
        suppress = true;
        eco.IsOn = on;
        suppress = false;
    }

    async void Eco_Toggled(object sender, RoutedEventArgs e)
    {
        if (suppress || group is not { } g) return;
        bool want = eco.IsOn;
        var pids = (ProcessMonitor.Find(g.Key)?.Pids ?? g.Pids).ToList();
        int ticket = ++ecoTicket;
        int ok = await Task.Run(() => ProcessControl.SetEfficiency(pids, want));
        bool now = await Task.Run(() => ProcessControl.IsEfficient(pids));
        if (ticket != ecoTicket) return;
        suppress = true;
        eco.IsOn = now;
        suppress = false;
        if (ok == 0 && Root.XamlRoot is { } root)
            await new ContentDialog
            {
                XamlRoot = root,
                Title = Strings.L("Efficiency mode couldn't be changed"),
                Content = Strings.L("Windows doesn't allow it for %@.", ProcessText.NameOf(g)),
                CloseButtonText = Strings.L("OK"),
            }.ShowAsync();
    }
}
