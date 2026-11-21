using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Fusebox.Core;
using Fusebox.Core.Processes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Fusebox.App.Pages;

/// <summary>One list row. Updated in place (INotifyPropertyChanged) so the list is never rebuilt on a tick.</summary>
public sealed partial class ProcRow : INotifyPropertyChanged
{
    string name = "", cpu = "", ram = "", gpu = "";
    ImageSource? icon;
    double rowOpacity = 1;

    public ProcRow(string key) => Key = key;
    public string Key { get; }
    public AppGroup? Group { get; private set; }
    public string Name { get => name; private set => Set(ref name, value); }
    public string Cpu { get => cpu; private set => Set(ref cpu, value); }
    public string Ram { get => ram; private set => Set(ref ram, value); }
    public string Gpu { get => gpu; private set => Set(ref gpu, value); }
    public ImageSource? Icon { get => icon; private set => Set(ref icon, value); }
    public double RowOpacity { get => rowOpacity; private set => Set(ref rowOpacity, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public void Update(AppGroup g)
    {
        Group = g;
        Name = g.Name;
        Cpu = ProcessText.Pct(g.Cpu);
        Ram = Fmt.Bytes(g.Ram);
        Gpu = g.Gpu >= 0.05 ? ProcessText.Pct(g.Gpu) : "–";
        RowOpacity = 1;
        Icon ??= ProcessMonitor.IconFor(g.ExePath);
    }

    public void UpdateInstalled(string appName)
    {
        Name = appName; Cpu = Strings.L("Not running"); Ram = ""; Gpu = ""; RowOpacity = 0.6;
    }
}

public sealed partial class ProcessesPage : Page
{
    readonly ObservableCollection<ProcRow> items = [];
    readonly Dictionary<string, ProcRow> rows = [];
    AppGrouper.SortBy sortBy = AppGrouper.SortBy.Cpu;
    bool sortDescending = true;
    string query = "";
    string? selectedKey;
    List<string>? installed;
    bool loadingInstalled;

    public ProcessesPage()
    {
        InitializeComponent();
        ProcList.ItemsSource = items;
        Loaded += (_, _) =>
        {
            Loc.Apply(this);
            StopBtn.Content = "✕ " + Strings.L("Stop");
            UpdateHeaders();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
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

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        query = SearchBox.Text.Trim();
        if (query.Length > 0 && installed == null && !loadingInstalled)
        {
            loadingInstalled = true;
            _ = LoadInstalled();
        }
        Render();
    }

    async Task LoadInstalled()
    {
        installed = await Task.Run(Fusebox.Platform.InstalledApps.All);
        Render();
    }

    void Filter_Changed(object sender, SelectionChangedEventArgs e) => Render();

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        var by = Enum.Parse<AppGrouper.SortBy>((string)((FrameworkElement)sender).Tag);
        if (by == sortBy) sortDescending = !sortDescending;
        else { sortBy = by; sortDescending = by != AppGrouper.SortBy.Name; }
        UpdateHeaders();
        Render();
    }

    void UpdateHeaders()
    {
        string Label(string text, AppGrouper.SortBy by) => text + (by == sortBy ? (sortDescending ? " ▾" : " ▴") : "");
        HeadName.Content = Label(Strings.L("Name"), AppGrouper.SortBy.Name);
        HeadCpu.Content = Label("CPU", AppGrouper.SortBy.Cpu);
        HeadRam.Content = Label("RAM", AppGrouper.SortBy.Ram);
        HeadGpu.Content = Label("GPU", AppGrouper.SortBy.Gpu);
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcList.SelectedItem is ProcRow r) selectedKey = r.Key;
        else if (e.AddedItems.Count == 0 && e.RemovedItems.Count > 0 && !reordering) selectedKey = null;
        RenderDetail();
    }

    bool reordering;

    void Render()
    {
        if (ProcessMonitor.Last is not { } snap) return;
        int kind = KindFilter.SelectedIndex;   // 0 all, then AppKind order + 1: apps, background, windows
        var shown = snap.Groups.Where(g => (kind == 0 || (int)g.Kind == kind - 1)
                                           && ProcessSearch.Matches(g.Name, query));
        var wanted = new List<ProcRow>();
        foreach (var g in AppGrouper.Sort(shown, sortBy, sortDescending))
        {
            if (!rows.TryGetValue(g.Key, out var r)) rows[g.Key] = r = new ProcRow(g.Key);
            r.Update(g);
            wanted.Add(r);
        }
        if (installed != null && kind == 0)
            foreach (var n in ProcessSearch.NotRunning(installed, snap.Groups.Select(g => g.Name), query).Take(30))
            {
                if (!rows.TryGetValue("installed:" + n, out var r)) rows["installed:" + n] = r = new ProcRow("installed:" + n);
                r.UpdateInstalled(n);
                wanted.Add(r);
            }

        reordering = true;
        var keep = wanted.ToHashSet();
        for (int i = items.Count - 1; i >= 0; i--)
            if (!keep.Contains(items[i])) { rows.Remove(items[i].Key); items.RemoveAt(i); }
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], wanted[i])) continue;
            int cur = items.IndexOf(wanted[i]);
            if (cur >= 0) items.Move(cur, i); else items.Insert(i, wanted[i]);
        }
        if (selectedKey != null && rows.TryGetValue(selectedKey, out var sel) && !ReferenceEquals(ProcList.SelectedItem, sel)) ProcList.SelectedItem = sel;
        reordering = false;

        OwnText.Text = Strings.L("Fusebox itself: CPU %@ · RAM %@", ProcessText.Pct(snap.OwnCpu), Fmt.Bytes(snap.OwnRam));
        RenderDetail();
    }

    void RenderDetail()
    {
        var row = selectedKey != null && rows.TryGetValue(selectedKey, out var r) ? r : null;
        DetHint.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        DetPanel.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        if (row == null) return;

        DetName.Text = row.Name;
        DetIcon.Source = row.Icon;
        DetLines.Children.Clear();
        if (row.Group is not { } g)
        {
            DetStatus.Text = Strings.L("Not running");
            DetailsBtn.Visibility = StopBtn.Visibility = Visibility.Collapsed;
            return;
        }
        double ramTotal = App.Live.Last?.RamTotal ?? double.NaN;
        var verdict = ProcessText.Judge(g, ramTotal);
        DetStatus.Text = ProcessText.VerdictText(verdict);
        bool light = ActualTheme == ElementTheme.Light;   // same colours as Themes/Fusebox.xaml (theme dictionary keys are not reliably reachable from code)
        DetStatus.Foreground = new SolidColorBrush(verdict == ProcessText.Verdict.Normal
            ? Windows.UI.Color.FromArgb(255, light ? (byte)0x21 : (byte)0x30, light ? (byte)0x83 : (byte)0xA4, light ? (byte)0x58 : (byte)0x6C)
            : Windows.UI.Color.FromArgb(255, light ? (byte)0xB0 : (byte)0xF7, light ? (byte)0x7A : (byte)0xB5, 0));
        foreach (var line in ProcessText.Lines(g, ProcessMonitor.Last?.Cpus ?? Environment.ProcessorCount, double.IsNaN(ramTotal) ? 0 : ramTotal, DateTime.Now))
            DetLines.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        if (g.Kind == AppKind.Windows)
            DetLines.Children.Add(new TextBlock { Text = Strings.L("Windows protects these parts, so they can't be stopped here."), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        DetailsBtn.Visibility = Visibility.Visible;
        StopBtn.Visibility = g.CanStop ? Visibility.Visible : Visibility.Collapsed;
    }

    void Details_Click(object sender, RoutedEventArgs e)
    {
        if (selectedKey != null && rows.TryGetValue(selectedKey, out var r) && r.Group != null) Frame.Navigate(typeof(AppPage), selectedKey);
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (selectedKey != null && rows.TryGetValue(selectedKey, out var r) && r.Group is { } g) await StopFlow.RunAsync(XamlRoot, g);
    }
}
