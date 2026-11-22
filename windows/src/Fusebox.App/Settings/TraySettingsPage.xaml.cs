using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// Settings › Tray icon (the Mac's "Menu bar", FEATURES.md §9.11): six icon styles with previews, one icon per measurement,
/// which measurements (max 4), what a click opens, and what the panel or menu contains.
/// </summary>
public sealed partial class TraySettingsPage : Page
{
    public TraySettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        Body.Children.Add(Section("In the system tray"));
        var dockNote = Kit.CaptionText(L("\"Don't show\" needs the taskbar button on, otherwise you can't find Fusebox."));
        void Note() => dockNote.Visibility = Prefs.Bool("dockIcon") ? Visibility.Collapsed : Visibility.Visible;
        Note();
        Kit.WhilePrefs(dockNote, k => { if (k == "dockIcon") Note(); });
        dockNote.Margin = new Thickness(2, 8, 2, 0);
        var styles = new StackPanel();
        styles.Children.Add(Parts.TrayStyleCards());
        styles.Children.Add(dockNote);
        Body.Children.Add(Block(styles));
        var split = Card("Each measurement in its own spot (max. 3)", "", Toggle("menubarSplit"), "");
        Body.Children.Add(split);

        Body.Children.Add(Section("Measurements in the tray (max. 4)"));
        Body.Children.Add(Block(MetricsEditor.Create("menubarMetrics", limit: 4)));

        Body.Children.Add(Section("When you click it"));
        var details = new StackPanel { Spacing = 4 };
        void Details()
        {
            details.Children.Clear();
            bool panel = Prefs.Str("menuStyle") == "panel", rich = Prefs.Bool("richPanel");
            if (panel) details.Children.Add(Card("Rich tray panel", "Health score, live values, the most important tip and quick buttons.", Toggle("richPanel", Details)));
            if (!panel || !rich)
            {
                foreach (var (id, header) in new[] { ("status", "How it's doing (one sentence)"), ("values", "All values"), ("top", "3 heaviest apps"), ("links", "Shortcuts") })
                    details.Children.Add(PartToggle(id, header));
            }
        }
        Body.Children.Add(Card("Type", "", Choice("menuStyle", [("menu", L("Regular menu")), ("panel", L("Panel with live values"))], Details), ""));
        Body.Children.Add(details);
        Details();
    }

    /// <summary>One part of the regular menu / classic panel; the pref lists what is on, in order.</summary>
    static SettingsCard PartToggle(string id, string header)
    {
        var t = new ToggleSwitch { IsOn = Prefs.List("menuParts").Contains(id), MinWidth = 0 };
        t.Toggled += (_, _) =>
        {
            var l = Prefs.List("menuParts").Where(x => x != id).ToList();
            if (t.IsOn) l.Add(id);
            string[] order = ["status", "values", "top", "links"];
            Prefs.SetList("menuParts", order.Where(l.Contains));
        };
        return new SettingsCard { Header = L(header), Action = t };
    }
}
