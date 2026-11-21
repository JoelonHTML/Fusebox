using System.Diagnostics;
using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Privacy (FEATURES.md §9.15). Has (from v1): log folder + open, ping on/off + host. Added: keep logs (keepDays).
/// To do (Settings agent): "Delete all logs…" with confirmation (CsvLog.DeleteAll), folder shown with ~ for the user folder.
/// </summary>
public sealed partial class PrivacySettingsPage : Page
{
    public PrivacySettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);
        Body.Children.Add(Section("Measurement history"));
        var keep = new ComboBox { MinWidth = 180 };
        (long Days, string Label)[] options = [(7, L("1 week")), (30, L("30 days")), (90, L("3 months")), (365, L("1 year")), (0, L("Forever"))];
        foreach (var o in options) keep.Items.Add(o.Label);
        keep.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Days == Prefs.Long("keepDays")));
        keep.SelectionChanged += (_, _) => { if (keep.SelectedIndex >= 0) Prefs.Set("keepDays", options[keep.SelectedIndex].Days); };
        Body.Children.Add(Card("Keep logs", "Fusebox always saves a small file per day on your PC, so Checkup can look back.", keep, "\uE81C"));
        var open = new Button { Content = L("Open log folder") };
        open.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(CsvLog.Dir); Process.Start(new ProcessStartInfo(CsvLog.Dir) { UseShellExecute = true }); } catch { }
        };
        var folder = Card("Folder", "", open, "\uE8B7");
        folder.SetDescriptionText(CsvLog.Dir);
        Body.Children.Add(folder);

        Body.Children.Add(Section("Internet"));
        var host = new TextBox { Text = Prefs.Str("pingHost"), MinWidth = 180 };
        var ping = Card("Measure internet latency (ping)", "", null, "\uE701");
        var toggle = new ToggleSwitch { IsOn = Prefs.Str("pingHost").Length > 0 };
        void Show()
        {
            host.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            ping.SetDescriptionText(toggle.IsOn ? L("One short connection to this server every 10 s, port 443. Nothing else goes to the internet.")
                                                : L("Off: Fusebox makes no internet connections at all."));
        }
        toggle.Toggled += (_, _) => { Prefs.Set("pingHost", toggle.IsOn ? "1.1.1.1" : ""); host.Text = Prefs.Str("pingHost"); Show(); };
        host.LostFocus += (_, _) => { var h = host.Text.Trim(); if (h.Length > 0 && h.Length <= 200) Prefs.Set("pingHost", h); };
        ping.Action = toggle;
        Show();
        Body.Children.Add(ping);
        Body.Children.Add(Card("Measure to", "", host));
    }
}
