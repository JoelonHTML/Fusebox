using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Fusebox.App;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Waits for an updating/restarting predecessor (--wait-pid), loads the language, offers "Move to Programs".
        Boot.BeforeUi(args);

        // Single instance: a second start hands over to the running one (which shows its window) and exits.
        var key = AppInstance.FindOrRegisterForKey("Fusebox");
        if (!key.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            using var done = new ManualResetEventSlim();
            // Redirecting from an STA thread can deadlock: do it on a pool thread and wait.
            Task.Run(async () => { try { await key.RedirectActivationToAsync(activation); } finally { done.Set(); } });
            done.Wait(TimeSpan.FromSeconds(10));
            return 0;
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            try
            {
                var app = new App();
                app.UnhandledException += (_, e) => CrashLog(e.Exception);
            }
            catch (Exception e) { CrashLog(e); throw; }
        });
        return 0;
    }

    /// <summary>Last crash in %LOCALAPPDATA%\Fusebox\crash.log (CI prints it; users can send it).</summary>
    public static void CrashLog(Exception e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fusebox");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O}\n{e}\n\n");
        }
        catch { }
    }
}
