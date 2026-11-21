using Fusebox.Core;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fusebox.App.Pages;

/// <summary>Details of one app: live graphs (only while this page is on screen), history, and facts. Opened with the app key as parameter.</summary>
public sealed partial class AppPage : Page
{
    string key = "";
    AppGroup? app;   // last known state, kept when the app quits
    (string Name, string? Company, string? Version) meta;
    string? metaPath;

    public AppPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Loc.Apply(this);
            StopBtn.Content = "✕ " + Strings.L("Stop");
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        key = e.Parameter as string ?? "";
        App.Live.Updated += OnTick;
        ProcessMonitor.Changed += Render;
        ProcessMonitor.Refresh(true);
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        App.Live.Updated -= OnTick;
        ProcessMonitor.Changed -= Render;
    }

    void OnTick() => ProcessMonitor.Refresh();

    void Back_Click(object sender, RoutedEventArgs e) { if (Frame.CanGoBack) Frame.GoBack(); }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (app != null) await StopFlow.RunAsync(XamlRoot, app);
    }

    void Render()
    {
        var live = ProcessMonitor.Find(key);
        if (live != null) app = live;
        if (app is not { } g) { NameText.Text = Strings.L("Not running"); return; }

        if (metaPath != g.ExePath || metaPath == null) { metaPath = g.ExePath; meta = ProcessSampler.Describe(g.ExePath, g.ImageName); }
        NameText.Text = g.Name;
        IconImg.Source = ProcessMonitor.IconFor(g.ExePath);
        string version = meta.Version != null ? Strings.L("Version %@ · ", meta.Version) : "";
        SubText.Text = version + (live == null ? Strings.L("Not running")
            : g.StartedLocal is { } s ? Strings.L("Running for %@", ProcessText.Duration(Math.Max((int)(DateTime.Now - s).TotalMinutes, 1))) : Strings.L("Running"));

        double ramTotal = App.Live.Last?.RamTotal ?? 0;
        StatusText.Text = ProcessText.VerdictText(ProcessText.Judge(g, ramTotal));
        StopBtn.Visibility = g.CanStop && live != null ? Visibility.Visible : Visibility.Collapsed;

        CpuNow.Text = ProcessText.Pct(g.Cpu);
        RamNow.Text = Fmt.Bytes(g.Ram);
        GpuNow.Text = ProcessText.Pct(g.Gpu);
        if (ProcessMonitor.History.Get(key) is { } h)
        {
            CpuSpark.SetValues(h.Cpu.Tail(h.Cpu.Count).ToList(), Math.Max(10, h.Cpu.Max));
            RamSpark.SetValues(h.Ram.Tail(h.Ram.Count).ToList());
            GpuSpark.SetValues(h.Gpu.Tail(h.Gpu.Count).ToList(), Math.Max(10, h.Gpu.Max));
            CpuStats.Text = Stats(ProcessText.Pct(h.CpuAvg), ProcessText.Pct(h.CpuPeak));
            RamStats.Text = Stats(Fmt.Bytes(h.RamAvg), Fmt.Bytes(h.RamPeak));
            GpuStats.Text = Stats(ProcessText.Pct(h.GpuAvg), ProcessText.Pct(h.GpuPeak));
        }

        Facts.Children.Clear();
        AddFact(Strings.L("Location"), g.ExePath ?? "–");
        AddFact(Strings.L("Publisher"), meta.Company ?? "–");
        AddFact(Strings.L("Version"), meta.Version ?? "–");
        AddFact(Strings.L("Started"), g.StartedLocal?.ToString("g", Strings.Culture) ?? "–");
        AddFact(Strings.L("Processes"), live == null ? "–" : g.Pids.Count.ToString());
        AddFact(Strings.L("Threads"), live == null || g.Threads == 0 ? "–" : g.Threads.ToString());

        WhatPanel.Children.Clear();
        if (live != null && g.Explanation.Length > 0)
            WhatPanel.Children.Add(new TextBlock { Text = Strings.L("What is this? %@.", g.Explanation), TextWrapping = TextWrapping.Wrap });
    }

    static string Stats(string avg, string peak) => Strings.L("Average %@ · peak %@", avg, peak);

    void AddFact(string label, string value)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, Opacity = 0.7 };
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(v, 1);
        row.Children.Add(l);
        row.Children.Add(v);
        Facts.Children.Add(row);
    }
}
