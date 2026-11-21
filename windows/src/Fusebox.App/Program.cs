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
