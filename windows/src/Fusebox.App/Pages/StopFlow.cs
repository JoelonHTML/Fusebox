using Fusebox.Core;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Pages;

/// <summary>Stop an app: ask its windows to close (WM_CLOSE), wait ~5 s, then ask the user before TerminateProcess.</summary>
public static class StopFlow
{
    static bool running;

    public static async Task RunAsync(XamlRoot root, AppGroup app)
    {
        if (running || !app.CanStop) return;
        running = true;
        try
        {
            var pids = app.Pids.ToList();
            bool asked = await Task.Run(() => ProcessControl.RequestClose(pids)) > 0;
            if (asked)
                for (int i = 0; i < 20; i++)   // 20 × 250 ms = 5 s
                {
                    await Task.Delay(250);
                    if (await Task.Run(() => ProcessControl.AllExited(pids))) { ProcessMonitor.Refresh(true); return; }
                }

            var ask = new ContentDialog
            {
                XamlRoot = root,
                Title = Strings.L("Stop %@?", app.Name),
                Content = Strings.L("Unsaved work in %@ may be lost.", app.Name),
                PrimaryButtonText = Strings.L("Force quit"),
                CloseButtonText = Strings.L("Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ask.ShowAsync() != ContentDialogResult.Primary) return;

            var now = ProcessMonitor.Find(app.Key)?.Pids ?? pids;   // helpers may have appeared meanwhile
            bool ok = await Task.Run(() => ProcessControl.Terminate(now));
            await Task.Delay(300);
            ProcessMonitor.Refresh(true);
            if (!ok && !await Task.Run(() => ProcessControl.AllExited(now)))
                await new ContentDialog
                {
                    XamlRoot = root,
                    Title = Strings.L("%@ couldn't be stopped", app.Name),
                    Content = Strings.L("Windows doesn't allow it, or the app isn't responding."),
                    CloseButtonText = Strings.L("OK"),
                }.ShowAsync();
        }
        finally { running = false; }
    }
}
