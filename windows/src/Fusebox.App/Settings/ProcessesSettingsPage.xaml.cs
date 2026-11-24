using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>Settings › Processes (FEATURES.md §9.7): what opens first, which columns, how small items are hidden.</summary>
public sealed partial class ProcessesSettingsPage : Page
{
    public ProcessesSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        Body.Children.Add(Section("When you open Processes"));
        Body.Children.Add(Card("Show", "", Choice("procFilter", [("all", L("All")), ("apps", L("Apps")), ("background", L("Background")), ("macos", "Windows")]), ""));
        Body.Children.Add(Card("Sort by", "", Choice("procSort", [("cpu", "CPU"), ("ram", L("Memory")), ("energy", L("Energy")), ("gpu", "GPU"), ("name", L("Name"))]), ""));

        (string Id, string Label)[] columns = [("disk", L("Disk")), ("net", L("Network")), ("energy", L("Energy")), ("gpu", "GPU"), ("threads", L("Threads")), ("running", L("Running"))];
        var preview = new ContentPresenter();
        void Redraw()
        {
            string sort = Prefs.Str("procSort");
            var cols = new List<(string, string, bool)> { ("CPU", "12%", sort == "cpu"), ("RAM", "1.4 GB", sort == "ram") };
            if (Shown("disk")) cols.Add((L("Disk"), "1.2 MB/s", false));
            if (Shown("net")) cols.Add((L("Network"), "↓ 80 KB/s", false));
            if (Shown("energy")) cols.Add((L("Energy"), "1.3 W", sort == "energy"));
            if (Shown("gpu")) cols.Add(("GPU", "4%", sort == "gpu"));
            if (Shown("threads")) cols.Add((L("Threads"), "32", false));
            if (Shown("running")) cols.Add((L("Running"), "2 h", false));
            preview.Content = Previews.TablePreview("Chrome", cols.ToArray(), sort == "name");
        }
        Redraw();
        Kit.WhileTheme(preview, Redraw);
        Kit.WhilePrefs(preview, k => { if (k is "procHide" or "netPerApp" or "procSort") Redraw(); });
        // They store what is off, so a newly added column is on. Network is its own setting (netPerApp, off by default: it costs a little).
        var list = CheckList("Columns", "\uE9D5", columns, Shown, (id, on) =>
        {
            if (id == "net") { Prefs.Set("netPerApp", on); return; }
            var l = Prefs.List("procHide").Where(x => x != id).ToList();
            if (!on) l.Add(id);
            Prefs.SetList("procHide", l);
        }, Parts.Capped(640, preview),
            Kit.CaptionText(L("Columns appear when the window is wide enough. Network: download and upload per app, read on this PC only while you look at Fusebox. Also shows the busiest app on the Live NET card.")));
        list.Margin = new Thickness(0, 20, 0, 0);
        Body.Children.Add(list);

        Body.Children.Add(Section("Small items"));
        hide = Card("Hide", "", Choice("procSmall", [("all", L("Nothing")), ("idle", L("Idle")), ("light", L("Under 1% CPU"))], Note), "");
        Body.Children.Add(hide);
        Note();
    }

    static bool Shown(string id) => id == "net" ? Prefs.Bool("netPerApp") : !Prefs.List("procHide").Contains(id);

    readonly SettingsCard hide;

    void Note() => hide.SetDescriptionText(Prefs.Str("procSmall") switch
    {
        "all" => L("Every process is listed."),
        "light" => L("Hides items under 1% CPU and under 200 MB memory. \"Show all\" brings them back."),
        _ => L("Hides items using almost no CPU (< 0.05%) and under 20 MB memory. \"Show all\" brings them back."),
    });
}
