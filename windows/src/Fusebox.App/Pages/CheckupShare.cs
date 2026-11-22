using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Platform.Checkup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>Export bar actions (§5.9): "Share report…" sheet (scrubbed text: Copy / Save as .txt) and "Export CSV". PDF is not offered on Windows.</summary>
internal static class CheckupShare
{
    /// <summary>The scrubbed report text, built off the UI thread.</summary>
    public static Task<string> BuildTextAsync(CheckupBundle b)
    {
        var live = App.Live;
        var last = live.Last;
        var batt = last != null && double.IsFinite(last.BatteryHealth) ? last : null;
        return Task.Run(() =>
        {
            var info = new ReportInfo
            {
                Processor = live.Hardware.CpuName, MemoryGb = (last?.RamTotal ?? double.NaN) / 1073741824.0, Windows = CheckupReader.WindowsVersion(),
                UptimeMinutes = (int)(Environment.TickCount64 / 60_000), Boot = CheckupReader.Boot(),
                DiskTotalGb = (last?.DiskTotal ?? double.NaN) / 1e9, DiskFreeGb = (last?.DiskFree ?? double.NaN) / 1e9,
                Battery = batt == null ? null : string.Join(" · ", new[] { $"{Fmt.Pct(batt.BatteryHealth)} of original capacity", double.IsFinite(batt.BatteryCycles) ? $"{(long)batt.BatteryCycles} cycles" : "" }.Where(s => s.Length > 0)),
            };
            return Scrubber.Scrub(ReportText.Build(b.Report, info, b.Tips, DateTime.Now), CheckupReader.Identity());
        });
    }

    public static async Task ShareAsync(XamlRoot root, CheckupBundle b) =>
        await ShowSheetAsync(root, L("Share report"), L("Contains no user name, file names, computer name, serial number or network addresses."), await BuildTextAsync(b), "fusebox-report");

    /// <summary>Sheet for already scrubbed text: Copy / Save as .txt / Close. Also used by the Network report.</summary>
    public static async Task ShowSheetAsync(XamlRoot root, string title, string noteText, string text, string fileName)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11,
            Height = 340, MinWidth = 420,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var note = new TextBlock { Text = noteText, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var status = new TextBlock { FontSize = 12, Opacity = 0.7 };
        var copy = new Button { Content = L("Copy") };
        var save = new Button { Content = L("Save as .txt…") };
        copy.Click += (_, _) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            status.Text = L("Copied");
        };
        save.Click += async (_, _) => status.Text = await SaveAsync(fileName, ".txt", L("Text"), text) ?? "";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(copy); row.Children.Add(save); row.Children.Add(status);
        status.VerticalAlignment = VerticalAlignment.Center;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(note); content.Children.Add(box); content.Children.Add(row);
        await new ContentDialog { XamlRoot = root, Title = title, Content = content, CloseButtonText = L("Close"), DefaultButton = ContentDialogButton.Close }.ShowAsync();
    }

    /// <summary>All main logs of the period, merged in the current column order.</summary>
    public static async Task ExportCsvAsync(XamlRoot root, Period p)
    {
        var now = DateTime.Now;
        var csv = await Task.Run(() =>
        {
            var texts = new List<string>();
            foreach (var d in Periods.Days(p))
            {
                var f = CsvLog.FileFor(now.Date.AddDays(-d));
                try { if (File.Exists(f)) texts.Add(DayLoader.ReadShared(f)); } catch (IOException) { }
            }
            return ReportText.MergeCsv(texts);
        });
        var error = await SaveAsync("fusebox-" + Periods.FileName(p), ".csv", "CSV", csv);
        if (error != null && error != "")
            await new ContentDialog { XamlRoot = root, Title = L("Saving failed: %@", error), CloseButtonText = L("OK") }.ShowAsync();
    }

    /// <summary>Save picker + write. Returns null when saved or cancelled; an error text (empty = nothing to say) otherwise. For the share sheet a short "Saved" is shown by the caller.</summary>
    static async Task<string?> SaveAsync(string name, string ext, string typeName, string content)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = name };
            picker.FileTypeChoices.Add(typeName, [ext]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Main));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return null;
            await Task.Run(() => File.WriteAllText(file.Path, content, new System.Text.UTF8Encoding(false)));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return e.Message; }
    }
}
