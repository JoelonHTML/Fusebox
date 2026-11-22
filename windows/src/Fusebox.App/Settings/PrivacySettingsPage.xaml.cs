using System.Diagnostics;
using Fusebox.Core;
using Fusebox.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>Settings › Privacy (FEATURES.md §9.15): how long logs are kept, where they are, delete them, and the one optional internet connection (ping).</summary>
public sealed partial class PrivacySettingsPage : Page
{
    public PrivacySettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        Body.Children.Add(Section("Measurement history"));
        Body.Children.Add(Card("Keep logs", "", LongChoice("keepDays", [(7, L("1 week")), (30, L("30 days")), (90, L("3 months")), (365, L("1 year")), (0, L("Forever"))]), ""));

        var open = new Button { Content = L("Open log folder") };
        open.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(CsvLog.Dir); Process.Start(new ProcessStartInfo(CsvLog.Dir) { UseShellExecute = true }); } catch { }
        };
        var folder = Card("Folder", "", open, "");
        folder.SetDescriptionText(SettingsText.Tilde(CsvLog.Dir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Body.Children.Add(folder);

        var delete = new Button { Content = L("Delete all logs…") };   // the confirmation dialog is the safety
        delete.Click += async (_, _) =>
        {
            if (await Confirm(XamlRoot, L("Delete all logs?"), L("All CSV files in the log folder will be removed. This can't be undone."), L("Delete")))
                await Task.Run(() => CsvLog.DeleteAll());
        };
        Body.Children.Add(Card("Delete all logs…", "", delete, ""));
        Footer(Body, L("Fusebox always saves a small file per day on your PC, so Checkup can look back."));

        Body.Children.Add(Section("Internet"));
        var host = new TextBox { Text = Prefs.Str("pingHost"), MinWidth = 200, MaxLength = 200 };
        var ping = Card("Measure internet latency (ping)", "", null, "");
        var toggle = new ToggleSwitch { IsOn = Prefs.Str("pingHost").Length > 0, MinWidth = 0 };
        var hostCard = Item(Card("Measure to", "", host));
        void Show()
        {
            host.IsEnabled = toggle.IsOn;
            ping.SetDescriptionText(toggle.IsOn ? L("One short connection to this server every 10 s, port 443. Nothing else goes to the internet.")
                                               : L("Off: Fusebox makes no internet connections at all."));
        }
        toggle.Toggled += (_, _) => { Prefs.Set("pingHost", toggle.IsOn ? "1.1.1.1" : ""); host.Text = Prefs.Str("pingHost"); Show(); };
        host.LostFocus += (_, _) => { var h = host.Text.Trim(); if (h.Length > 0 && h.Length <= 200) Prefs.Set("pingHost", h); else host.Text = Prefs.Str("pingHost"); };
        ping.Action = toggle;
        Body.Children.Add(Expand(ping, hostCard));
        Show();
    }
}
