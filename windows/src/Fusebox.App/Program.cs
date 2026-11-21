using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Fusebox.App;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Elevated one-off task (see Fusebox.Platform.Elevated): no UI at all.
        if (args.Length == 3 && args[0] == "--elevated") return Fusebox.Platform.Elevated.RunTask(args[1], args[2]);
        WinRT.ComWrappersSupport.InitializeComWrappers();

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

        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}
