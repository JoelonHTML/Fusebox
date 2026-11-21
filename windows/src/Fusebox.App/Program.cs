using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Fusebox.App;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Elevated one-off task (see Fusebox.Platform.Elevated): no UI at all.
        if (args.Length == 3 && args[0] == "--elevated") return Fusebox.Platform.Elevated.RunTask(args[1], args[2]);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}
