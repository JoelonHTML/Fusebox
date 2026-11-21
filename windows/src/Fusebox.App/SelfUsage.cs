using System.Diagnostics;
using System.Runtime.InteropServices;
using Fusebox.Core;
using Fusebox.Core.Settings;
using Microsoft.UI.Windowing;

namespace Fusebox.App;

/// <summary>
/// Fusebox's own use (§11.5): once a minute one line in Documents\Fusebox\self-YYYY-MM-DD.csv (own CPU of one core, private memory;
/// energy, GPU and wake-ups are not measured on Windows and stay empty), and "Share performance report" for Settings › About: a text file
/// in Downloads (also on the clipboard, shown in Explorer) with the last 7 days per situation plus 5 seconds measured right now.
/// Nothing is sent anywhere; the user name never appears (the home folder is "~").
/// </summary>
public static class SelfUsage
{
    static TimeSpan lastCpu;
    static DateTime lastMark = DateTime.MinValue;
    static bool started;

    public static void Start()
    {
        if (started) return;
        started = true;
        Mark();   // zero point
        App.Live.MinuteTick += Tick;
    }

    static (TimeSpan Cpu, double MemMb, int Threads) Read()
    {
        using var p = Process.GetCurrentProcess();
        return (p.TotalProcessorTime, p.PrivateMemorySize64 / 1048576.0, p.Threads.Count);
    }

    static void Mark() { lastCpu = Read().Cpu; lastMark = DateTime.UtcNow; }

    static void Tick()
    {
        try
        {
            var now = DateTime.UtcNow;
            double secs = (now - lastMark).TotalSeconds;
            var (cpu, mem, _) = Read();
            double pct = secs > 5 ? 100 * (cpu - lastCpu).TotalSeconds / secs : double.NaN;
            lastCpu = cpu; lastMark = now;
            if (double.IsNaN(pct)) return;
            var main = App.Main;
            var s = App.Live.Last;
            string window = "-";
            if (main != null)
            {
                double scale = Win32.Scale(WinRT.Interop.WindowNative.GetWindowHandle(main));
                window = $"{(int)(main.AppWindow.Size.Width / scale)}x{(int)(main.AppWindow.Size.Height / scale)}";
            }
            var row = new SelfRow(DateTime.Now, $"{BuildInfo.Version} ({BuildInfo.Build})", pct, mem, double.NaN, double.NaN, double.NaN, double.NaN,
                (main?.CurrentPage ?? "none").Replace("Page", ""), window, main?.IsOnScreen ?? false, Prefs.Str("theme"), Prefs.Str("font"), Prefs.Str("lowImpact"),
                s == null || double.IsNaN(s.Battery) ? "ac" : s.Charging ? "charging" : s.OnAc ? "ac" : "battery");
            var text = SelfLog.Line(row);
            var file = CsvLog.FileFor(DateTime.Now, "self");
            _ = Task.Run(() => CsvLog.Append(file, SelfLog.Header, text));
        }
        catch { }
    }

    /// <summary>Builds and saves the report. Takes about 5 seconds (the measurement of the present). Returns the file, or null when it can't be written.</summary>
    public static async Task<string?> ShareReport()
    {
        // Right now: 5 s of own CPU, then a few facts about the process.
        var before = Read();
        var t0 = DateTime.UtcNow;
        await Task.Delay(5000);
        var after = Read();
        double secs = (DateTime.UtcNow - t0).TotalSeconds;
        string now = $"Right now (5 s): {100 * (after.Cpu - before.Cpu).TotalSeconds / secs:0.00}% of one core · {after.MemMb:0} MB private memory · {after.Threads} threads"
            + $" · GC collections {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} · low impact {(LowImpact.On ? "on" : "off")}";
        var report = await Task.Run(() =>
        {
            var rows = new List<SelfRow>();
            var from = DateTime.Now.Date.AddDays(-6);
            for (var d = from; d <= DateTime.Now.Date; d = d.AddDays(1))
                try { rows.AddRange(SelfLog.Parse(File.ReadAllText(CsvLog.FileFor(d, "self")))); } catch { }
            var hw = App.Live.Hardware;
            int displays = 1;
            try { displays = DisplayArea.FindAll().Count; } catch { }
            string machine = $"{hw.CpuName} · Windows {Environment.OSVersion.Version} · {RuntimeInformation.ProcessArchitecture} · {Environment.ProcessorCount} cores"
                + $" · {(double.IsFinite(App.Live.Last?.RamTotal ?? double.NaN) ? $"{Math.Round(App.Live.Last!.RamTotal / 1e9)} GB" : "? GB")} · {displays} display(s)";
            var text = SelfLog.Report(rows, $"{BuildInfo.Version} ({BuildInfo.Build})", machine, now);
            // The report may name folders: the home folder is "~", and the user name goes with it.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0) text = text.Replace(home, "~", StringComparison.OrdinalIgnoreCase);
            return Environment.UserName.Length >= 3 ? text.Replace(Environment.UserName, "user", StringComparison.OrdinalIgnoreCase) : text;
        });
        try
        {
            var dir = Windows.Storage.UserDataPaths.GetDefault().Downloads;
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"Fusebox-performance-{DateTime.Now:yyyy-MM-dd}.txt");
            await File.WriteAllTextAsync(file, report);
            try
            {
                var pack = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pack.SetText(report);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pack);
            }
            catch { }
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = false }); } catch { }
            return file;
        }
        catch { return null; }
    }
}
