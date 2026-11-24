using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>Settings › Network (FEATURES.md §9.9): scanning, which device columns show (MAC is visible by default), speed test unit, ping link.</summary>
public sealed partial class NetworkSettingsPage : Page
{
    public NetworkSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        Body.Children.Add(Section("Devices"));
        Body.Children.Add(Card("Scan when you open Network", "Off: Fusebox only looks for devices on your network when you press Scan.", Toggle("netAutoScan"), ""));
        Body.Children.Add(Card("Notify me about new devices", "While Network scans. Same as the bell on the page.", Toggle("notifyNewDevice"), ""));

        var preview = new ContentPresenter();
        void Redraw()
        {
            var hide = Prefs.List("netHide");
            var cols = new List<(string, string, bool)> { ("IP", "192.168.1.5", false) };
            if (!hide.Contains("mac")) cols.Add(("MAC", "B8:27:EB:01:02:03", false));   // sample value, never a real address
            if (!hide.Contains("vendor")) cols.Add((L("Vendor"), "Raspberry Pi", false));
            if (!hide.Contains("kind")) cols.Add((L("Type"), L("Computer"), false));
            cols.Add(("ms", "3", false));
            preview.Content = Previews.TablePreview("Living Room", cols.ToArray());
        }
        Redraw();
        Kit.WhileTheme(preview, Redraw);
        Kit.WhilePrefs(preview, k => { if (k == "netHide") Redraw(); });
        var list = HiddenList("Columns", "\uE9D5", "netHide", [("mac", "MAC"), ("vendor", L("Vendor")), ("kind", L("Type"))],
            Parts.Capped(640, preview), Kit.CaptionText(L("Columns appear when the window is wide enough.")));
        list.Margin = new Thickness(0, 20, 0, 0);
        Body.Children.Add(list);

        Body.Children.Add(Section("Speed test"));
        var speed = Card("Unit", "", ChoiceBool("speedMB", "Mbit/s", "MB/s"), "");
        speed.SetDescriptionText(L("Mbit/s shows 412 Mbit/s · MB/s shows 51.5 MB/s"));
        Body.Children.Add(speed);

        Body.Children.Add(Section("Internet check"));
        var change = new Button { Content = L("Change in Privacy") };
        change.Click += (_, _) => SettingsWindow.GoTo("data");
        var ping = Card("Ping", "", change, "");
        ping.SetDescriptionText(Prefs.Str("pingHost") is { Length: > 0 } h ? L("On, to %@", h) : L("Off: Fusebox makes no internet connections"));
        Body.Children.Add(ping);
    }
}
