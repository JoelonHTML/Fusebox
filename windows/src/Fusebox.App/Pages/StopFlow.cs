using Fusebox.Core;
using Fusebox.Core.Processes;
using Fusebox.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fusebox.App.Pages;

/// <summary>
/// Stop / Quit / Force quit (FEATURES.md §4.9.1, §4.11.1). Confirm first, then ask the windows to close (WM_CLOSE) and wait ~5 s;
/// windowless processes are terminated right away. When a polite close didn't work, offer Force quit.
/// </summary>
public static class StopFlow
{
    public enum Mode { Stop, Quit, Force }

    static bool running;

    public static async Task RunAsync(XamlRoot root, AppGroup app, Mode mode = Mode.Stop)
    {
        if (running || !app.CanStop) return;
        running = true;
        try
        {
            string name = ProcessText.NameOf(app);
            if (!await Confirm(root, mode, name)) return;

            var pids = app.Pids.ToList();
            if (mode != Mode.Force)
            {
                bool asked = await Task.Run(() => ProcessControl.RequestClose(pids)) > 0;
                if (asked)
                {
                    for (int i = 0; i < 20; i++)   // 20 × 250 ms = 5 s
                    {
                        await Task.Delay(250);
                        if (await Task.Run(() => ProcessControl.AllExited(pids))) { ProcessMonitor.Refresh(true); return; }
                    }
                    // Still open (maybe a "save changes?" box): don't kill behind the user's back, ask once more.
                    if (!await Confirm(root, Mode.Force, name)) return;
                }
            }

            var now = ProcessMonitor.Find(app.Key)?.Pids ?? pids;   // helpers may have appeared meanwhile
            bool ok = await Task.Run(() => ProcessControl.Terminate(now));
            await Task.Delay(300);
            ProcessMonitor.Refresh(true);
            if (!ok && !await Task.Run(() => ProcessControl.AllExited(now)))
                await new ContentDialog
                {
                    XamlRoot = root,
                    Title = Strings.L("%@ couldn't be stopped", name),
                    Content = Strings.L("Windows doesn't allow it, or the app isn't responding."),
                    CloseButtonText = Strings.L("OK"),
                }.ShowAsync();
        }
        finally { running = false; }
    }

    static async Task<bool> Confirm(XamlRoot root, Mode mode, string name)
    {
        var ask = new ContentDialog
        {
            XamlRoot = root,
            Title = mode switch { Mode.Quit => Strings.L("Quit %@?", name), Mode.Force => Strings.L("Force quit %@?", name), _ => Strings.L("Stop %@?", name) },
            Content = Strings.L("Unsaved work in %@ may be lost.", name),
            PrimaryButtonText = mode switch { Mode.Quit => Strings.L("Quit"), Mode.Force => Strings.L("Force Quit"), _ => Strings.L("Stop") },
            CloseButtonText = Strings.L("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ask.ShowAsync() == ContentDialogResult.Primary;
    }
}
