using Fusebox.Core;
using Fusebox.Core.Docked;
using Fusebox.Platform.Docked;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Devices.Enumeration;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>
/// Reads when opened, on device changes (DeviceWatchers, debounced 1 s) and on Refresh. No polling.
/// Not covered: display plug/unplug (use Refresh), BLE GATT battery.
/// </summary>
public sealed partial class DockedPage : Page
{
    // USB device interface class, Bluetooth classic AEP protocol, audio output endpoints.
    const string UsbFilter = "System.Devices.InterfaceClassGuid:=\"{A5DCBF10-6530-11D2-901F-00C04FB951ED}\" AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";
    const string BtFilter = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\"";

    readonly List<DeviceWatcher> watchers = new();
    DispatcherQueueTimer? debounce;
    int generation;

    public DockedPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        debounce = DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromSeconds(1);
        debounce.IsRepeating = false;
        debounce.Tick += (_, _) => Reload();
        StartWatcher(DeviceInformation.CreateWatcher(UsbFilter, Array.Empty<string>(), DeviceInformationKind.DeviceInterface));
        StartWatcher(DeviceInformation.CreateWatcher(BtFilter, Array.Empty<string>(), DeviceInformationKind.AssociationEndpoint));
        StartWatcher(DeviceInformation.CreateWatcher(DeviceClass.AudioRender));
        Reload();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        generation++;   // drops a read that is still running
        debounce?.Stop();
        debounce = null;
        foreach (var w in watchers)
        {
            try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { }
        }
        watchers.Clear();
    }

    void StartWatcher(DeviceWatcher w)
    {
        // The initial Added burst is the existing devices: only react once enumeration has finished.
        bool ready = false;
        var queue = DispatcherQueue;   // watcher events come on thread-pool threads
        void Changed(DeviceWatcher s, object o)
        {
            if (ready) queue.TryEnqueue(() => { debounce?.Stop(); debounce?.Start(); });
        }
        w.Added += (s, i) => Changed(s, i);
        w.Removed += (s, i) => Changed(s, i);
        w.Updated += (s, i) => Changed(s, i);
        w.EnumerationCompleted += (s, o) => ready = true;
        watchers.Add(w);
        try { w.Start(); } catch { }
    }

    void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    void Reload()
    {
        int gen = ++generation;
        RefreshButton.IsEnabled = false;
        var queue = DispatcherQueue;
        Task.Run(() =>
        {
            DockedSnapshot? snap = null;
            try { snap = DockedReader.Read(); } catch { }
            queue.TryEnqueue(() =>
            {
                if (gen != generation) return;
                RefreshButton.IsEnabled = true;
                if (snap != null) Render(snap);
            });
        });
    }

    void Render(DockedSnapshot s)
    {
        PowerText.Text = PowerLine(s.Power);

        UsbList.Children.Clear();
        if (s.Usb.Count == 0) UsbList.Children.Add(Line(L("Nothing connected."), dim: true));
        foreach (var u in s.Usb) UsbList.Children.Add(UsbRow(u));

        DisplayList.Children.Clear();
        if (s.Displays.Count == 0) DisplayList.Children.Add(Line(L("No displays found."), dim: true));
        foreach (var d in s.Displays)
        {
            var res = d.Width > 0 ? $"{d.Width}×{d.Height}" : "";
            var parts = new List<string> { d.Connection };
            if (res != "") parts.Add(res);
            if (d.Hz > 0) parts.Add(DisplayMap.HzText(d.Hz));
            if (d.Hdr == true) parts.Add(L("HDR"));
            DisplayList.Children.Add(Row(d.Name, string.Join(" · ", parts)));
        }

        BluetoothCard.Visibility = s.Bluetooth == null ? Visibility.Collapsed : Visibility.Visible;
        BluetoothList.Children.Clear();
        if (s.Bluetooth is { } bt)
        {
            if (bt.Count == 0) BluetoothList.Children.Add(Line(L("No paired devices."), dim: true));
            foreach (var b in bt)
            {
                var parts = new List<string> { b.Connected ? L("Connected") : L("Bluetooth · paired, not connected") };
                if (b.Battery is int pct) parts.Add(L("Battery %lld%%", pct));
                BluetoothList.Children.Add(Row(b.Name, string.Join(" · ", parts)));
            }
        }

        AudioList.Children.Clear();
        if (s.Audio.Count == 0) AudioList.Children.Add(Line(L("No audio devices found."), dim: true));
        foreach (var a in s.Audio)
        {
            var parts = new List<string> { a.Kind, a.IsOutput ? L("Output") : L("Input") };
            if (a.IsDefault) parts.Add(L("Default"));
            if (!a.Plugged) parts.Add(L("Nothing plugged in"));
            AudioList.Children.Add(Row(a.Name, string.Join(" · ", parts)));
        }
    }

    static string PowerLine(PowerInfo p)
    {
        var parts = new List<string>();
        if (p.OnAc is bool ac) parts.Add(ac ? L("Plugged in") : L("Running on battery"));
        if (p.HasBattery && p.Percent is int pct) parts.Add(L("Battery %lld%%", pct) + (p.Charging ? " · " + L("charging") : ""));
        return parts.Count > 0 ? string.Join(" · ", parts) : L("Power status unavailable.");
    }

    UIElement UsbRow(UsbNode u)
    {
        var sub = new List<string>();
        if (u.Maker != null) sub.Add(u.Maker);
        if (u.Speed != null) sub.Add(u.Speed);
        if (u.TypeC == true) sub.Add("USB-C");
        var panel = new StackPanel { Margin = new Thickness(u.Depth * 20, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = (u.IsHub ? "▸ " : "") + u.Name, TextWrapping = TextWrapping.Wrap });
        if (sub.Count > 0) panel.Children.Add(Line(string.Join(" · ", sub), dim: true));
        if (u.Slow && u.Max != null)
        {
            var warn = Line(L("Runs slower than it can") + " (" + L("can do") + " " + u.Max + ")", dim: false);
            warn.Foreground = WarnProbe.Foreground;
            panel.Children.Add(warn);
        }
        return panel;
    }

    static UIElement Row(string title, string sub)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Line(sub, dim: true));
        return panel;
    }

    static TextBlock Line(string text, bool dim) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = dim ? 0.7 : 1 };
}
