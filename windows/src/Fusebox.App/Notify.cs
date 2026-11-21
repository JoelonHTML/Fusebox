using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Fusebox.App;

/// <summary>Windows notifications (Windows App SDK AppNotificationManager, works unpackaged). Registered once in Boot.</summary>
public static class Notify
{
    static bool registered;

    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.Register();
            registered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { AppNotificationManager.Default.Unregister(); } catch { } };
        }
        catch { /* no notifications available: Send becomes a no-op */ }
    }

    /// <summary>Follows the "Show notifications" switch (Prefs "notify", default on).</summary>
    public static void Send(string title, string body)
    {
        if (!registered || !Prefs.Get("notify", true)) return;
        try { AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(body).BuildNotification()); }
        catch { }
    }
}
