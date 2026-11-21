using Fusebox.App.Settings;
using Fusebox.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Fusebox.App;

/// <summary>
/// Windows notifications (Windows App SDK AppNotificationManager, works unpackaged). Registered once in Boot.
/// <see cref="Send"/> takes an optional click action and up to a few buttons; each is a verb with arguments that comes back through
/// <see cref="On"/> when the user clicks. Built-in verbs: <c>open</c> (arg "tab" = Live/Processes/Checkup/Network/Docked, optional "param")
/// brings the main window to that tab; <c>settings</c> (arg "tab" = a Settings tab id, e.g. about) opens Settings there.
/// Other features register their own, e.g. the peak alert's <c>stop</c> (args app, name): <c>Notify.On("stop", a =&gt; …)</c>.
/// </summary>
public static class Notify
{
    /// <summary>A button (<paramref name="Label"/> set) or the click on the notification itself (Label ignored).</summary>
    public sealed record Act(string Label, string Verb, params (string Key, string Value)[] Args);

    static bool registered;
    static readonly Dictionary<string, Action<IReadOnlyDictionary<string, string>>> handlers = [];

    public static void Register()
    {
        try
        {
            On("open", a => App.ShowMain(a.GetValueOrDefault("tab", "Live"), a.GetValueOrDefault("param")));
            On("settings", a => { App.ShowMain(); SettingsWindow.Open(a.GetValueOrDefault("tab", "about")); });
            AppNotificationManager.Default.NotificationInvoked += (_, e) =>
            {
                var args = e.Arguments.ToDictionary(k => k.Key, k => k.Value);
                if (!args.TryGetValue("verb", out var verb) || !handlers.TryGetValue(verb, out var h)) return;
                App.Main?.DispatcherQueue.TryEnqueue(() => { try { h(args); } catch { } });
            };
            AppNotificationManager.Default.Register();
            registered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { AppNotificationManager.Default.Unregister(); } catch { } };
        }
        catch { /* no notifications available: Send becomes a no-op */ }
    }

    /// <summary>Handler for a verb (runs on the UI thread). One handler per verb: the last registration wins.</summary>
    public static void On(string verb, Action<IReadOnlyDictionary<string, string>> handler) => handlers[verb] = handler;

    /// <summary>False when Windows (Settings › System › Notifications, Focus, group policy) keeps Fusebox's notifications off.</summary>
    public static bool Allowed
    {
        get { try { return !registered || AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled; } catch { return true; } }
    }

    /// <summary>Follows the "Show notifications" switch (Prefs "notify", default on).</summary>
    public static void Send(string title, string body, Act? click = null, params Act[] buttons)
    {
        if (!registered || !Prefs.Bool("notify")) return;
        try
        {
            var b = new AppNotificationBuilder().AddText(title).AddText(body);
            if (click != null) foreach (var (k, v) in Pairs(click)) b.AddArgument(k, v);
            foreach (var a in buttons)
            {
                var btn = new AppNotificationButton(a.Label);
                foreach (var (k, v) in Pairs(a)) btn.AddArgument(k, v);
                b.AddButton(btn);
            }
            AppNotificationManager.Default.Show(b.BuildNotification());
        }
        catch { }
    }

    static IEnumerable<(string, string)> Pairs(Act a) => a.Args.Prepend(("verb", a.Verb));

    /// <summary>The test notification of Settings › Notifications (also when Fusebox is in front).</summary>
    public static void SendTest() => Send(Strings.L("Fusebox update available (test)"), Strings.L("This is how Fusebox tells you about a new version."));
}
