using System.Globalization;
using Fusebox.Core;
using Fusebox.Core.Checkup;
using Fusebox.Core.Reports;
using Fusebox.Platform.Checkup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>
/// Export bar actions (§5.9): "Share report…" sheet (scrubbed text to copy, "Save as PDF…"), "Export CSV" and "Export PDF".
/// The PDF (Core/Reports) is built on a worker thread from what is already known; nothing new is measured.
/// </summary>
internal static class CheckupShare
{
    /// <summary>PC facts for the reports. Reads the registry and the last sample: call off the UI thread with values captured on it.</summary>
    static ReportInfo Info(LiveSample? last, string cpuName)
    {
        var batt = last != null && double.IsFinite(last.BatteryHealth) ? last : null;
        return new ReportInfo
        {
            Processor = cpuName, MemoryGb = (last?.RamTotal ?? double.NaN) / 1073741824.0, Windows = CheckupReader.WindowsVersion(),
            UptimeMinutes = (int)(Environment.TickCount64 / 60_000), Boot = CheckupReader.Boot(),
            DiskTotalGb = (last?.DiskTotal ?? double.NaN) / 1e9, DiskFreeGb = (last?.DiskFree ?? double.NaN) / 1e9,
            Battery = batt == null ? null : string.Join(" · ", new[] { $"{Fmt.Pct(batt.BatteryHealth)} of original capacity", double.IsFinite(batt.BatteryCycles) ? $"{(long)batt.BatteryCycles} cycles" : "" }.Where(s => s.Length > 0)),
        };
    }

    /// <summary>"Intel Core i7 · 16 GB · Windows 11 Pro 24H2" for a PDF header. Call on the UI thread; returns a function for the worker.</summary>
    public static Func<string> PcLine()
    {
        var last = App.Live.Last; var cpu = App.Live.Hardware.CpuName;
        return () => CheckupPdf.PcLine(Info(last, cpu));
    }

    /// <summary>The scrubbed report text, built off the UI thread.</summary>
    public static Task<string> BuildTextAsync(CheckupBundle b)
    {
        var last = App.Live.Last; var cpu = App.Live.Hardware.CpuName;
        return Task.Run(() => Scrubber.Scrub(ReportText.Build(b.Report, Info(last, cpu), b.Tips, DateTime.Now), CheckupReader.Identity()));
    }

    /// <summary>The Checkup PDF builder: captures what lives on the UI thread now, builds on the worker it is run on.</summary>
    static Func<byte[]> PdfBuilder(CheckupBundle b)
    {
        var last = App.Live.Last; var cpu = App.Live.Hardware.CpuName;
        int? startup = StartupCard.Count;
        double? battery = last != null && double.IsFinite(last.BatteryHealth) ? last.BatteryHealth : null;
        int? cycles = last != null && double.IsFinite(last.BatteryCycles) ? (int)last.BatteryCycles : null;
        return () =>
        {
            var minutes = DayLoader.LoadDays(b.Report.Period, b.Made).SelectMany(d => d.Minutes).ToList();   // cached per file: normally no re-read
            return CheckupPdf.Build(new CheckupPdfInput(b.Report, b.Tips, Info(last, cpu), minutes, b.Made, startup, battery, cycles), CheckupReader.Identity());
        };
    }

    static string PdfName(CheckupBundle b) => $"Fusebox-checkup-{Periods.FileName(b.Report.Period)}-{b.Made.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    public static async Task ShareAsync(XamlRoot root, CheckupBundle b) =>
        await ShowSheetAsync(root, L("Share report"), L("Contains no user name, file names, computer name, serial number or network addresses."), await BuildTextAsync(b),
            PdfName(b), PdfBuilder(b));

    /// <summary>"Export PDF": save picker, then the PDF is built off the UI thread and written; offers to open it.</summary>
    public static async Task ExportPdfAsync(XamlRoot root, CheckupBundle b)
    {
        var (path, error) = await SaveAsync(PdfName(b), ".pdf", "PDF", PdfBuilder(b));
        if (error != null)
            await new ContentDialog { XamlRoot = root, Title = L("Creating the PDF failed"), Content = error, CloseButtonText = L("OK") }.ShowAsync();
        else if (path != null
                 && await new ContentDialog { XamlRoot = root, Title = L("PDF saved"), PrimaryButtonText = L("Open"), CloseButtonText = L("Close"), DefaultButton = ContentDialogButton.Primary }.ShowAsync()
                 == ContentDialogResult.Primary)
            Shell.Open(path);
    }

    /// <summary>Sheet for already scrubbed text: Copy (the text) / Save as PDF… (then Open) / Close. Also used by the Network report.</summary>
    public static async Task ShowSheetAsync(XamlRoot root, string title, string noteText, string text, string pdfName, Func<byte[]> buildPdf)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11,
            Height = 340, MinWidth = 420,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var note = new TextBlock { Text = noteText, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var status = new TextBlock { FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        var copy = new Button { Content = L("Copy") };
        var save = new Button { Content = L("Save as PDF…") };
        var open = new HyperlinkButton { Content = L("Open"), Visibility = Visibility.Collapsed, Padding = new Thickness(4, 2, 4, 2) };
        string? saved = null;
        copy.Click += (_, _) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            status.Text = L("Copied");
        };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            var (path, error) = await SaveAsync(pdfName, ".pdf", "PDF", buildPdf);
            save.IsEnabled = true;
            if (error != null) { status.Text = L("Creating the PDF failed") + ": " + error; return; }
            if (path == null) return;
            saved = path;
            status.Text = L("Saved");
            open.Visibility = Visibility.Visible;
        };
        open.Click += (_, _) => { if (saved != null) Shell.Open(saved); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(copy); row.Children.Add(save); row.Children.Add(status); row.Children.Add(open);
        open.VerticalAlignment = VerticalAlignment.Center;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(note); content.Children.Add(box); content.Children.Add(row);
        await new ContentDialog { XamlRoot = root, Title = title, Content = content, CloseButtonText = L("Close"), DefaultButton = ContentDialogButton.Close }.ShowAsync();
    }

    /// <summary>All main logs of the period, merged in the current column order.</summary>
    public static async Task ExportCsvAsync(XamlRoot root, Period p)
    {
        var now = DateTime.Now;
        var (_, error) = await SaveAsync("fusebox-" + Periods.FileName(p), ".csv", "CSV", () =>
        {
            var texts = new List<string>();
            foreach (var d in Periods.Days(p))
            {
                var f = CsvLog.FileFor(now.Date.AddDays(-d));
                try { if (File.Exists(f)) texts.Add(DayLoader.ReadShared(f)); } catch (IOException) { }
            }
            return new System.Text.UTF8Encoding(false).GetBytes(ReportText.MergeCsv(texts));
        });
        if (error != null)
            await new ContentDialog { XamlRoot = root, Title = L("Saving failed: %@", error), CloseButtonText = L("OK") }.ShowAsync();
    }

    /// <summary>Save picker, then <paramref name="content"/> runs on a worker thread and is written. (null, null) = cancelled; Path = saved; Error = what went wrong.</summary>
    static async Task<(string? Path, string? Error)> SaveAsync(string name, string ext, string typeName, Func<byte[]> content)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = name };
            picker.FileTypeChoices.Add(typeName, [ext]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Main));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return (null, null);
            await Task.Run(() => File.WriteAllBytes(file.Path, content()));
            return (file.Path, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)   // picker, disk or a report bug: say so instead of taking the app down
        { return (null, e.Message); }
    }
}
