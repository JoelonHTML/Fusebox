using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Fusebox.App.Settings.SettingsUi;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>Settings › Checkup (FEATURES.md §9.8): which period opens first, which sections show, when to suggest a restart.</summary>
public sealed partial class CheckupSettingsPage : Page
{
    readonly TextBlock restartNote = Kit.CaptionText("");

    public CheckupSettingsPage()
    {
        InitializeComponent();
        SettingsUi.TranslateOnce(this);

        Body.Children.Add(Section("When you open Checkup"));
        Body.Children.Add(Card("Period", "", Segmented("checkupStart", [("last", L("Last used")), ("today", L("Today")), ("yesterday", L("Yesterday")), ("week", L("7 days"))]), ""));

        Body.Children.Add(Section("Sections"));
        var sections = new List<(string, string)>
        {
            ("score", L("Health score")), ("tips", L("Tips")), ("tops", L("Top apps")), ("peaks", L("Peaks")), ("busy", L("Busy moments")),
            ("background", L("Windows background")),
        };
        if (Parts.HasBattery) sections.AddRange([("battery", L("Battery")), ("health", L("Battery health")), ("drain", L("Battery drain")), ("sleep", L("Sleep drain"))]);
        sections.AddRange([("awake", L("Keeps awake")), ("crashes", L("Crashes")), ("startup", L("Startup items")), ("disk", L("Disk space")),
                           ("ssdBench", L("SSD speed test")), ("runtime", L("Running time")), ("log", L("Log"))]);
        Body.Children.Add(HiddenChips("checkupHide", sections.ToArray()));
        Footer(Body, L("\"Why is my PC slow?\" is always there. A category with all its sections off disappears. Exported PDFs always contain everything."));

        Body.Children.Add(Section("Restart reminder"));
        Body.Children.Add(Card("Suggest a restart after", "", LongChoice("restartDays", [(7, L("7 days")), (14, L("14 days")), (30, L("30 days")), (0, L("Never"))], Note), ""));
        Body.Children.Add(restartNote);
        Note();
    }

    void Note()
    {
        long d = Prefs.Long("restartDays");
        restartNote.Text = d == 0 ? L("Checkup never suggests a restart.")
            : L("A tip in Checkup: \"Not restarted for %lld days. A restart clears memory and finishes updates.\"", d);
    }
}
