using System.Security.Principal;
using Fusebox.Core;
using Fusebox.Core.Checkup;
using static Fusebox.Core.Strings;

namespace Fusebox.Platform.Checkup;

/// <summary>
/// "What takes my disk space?" (§5.B.2 on Windows): the biggest folders of the user profile and of AppData\Local, Roaming and LocalLow, plus
/// the Recycle Bin, Windows Update downloads and Delivery Optimization cache when readable. Nothing is deleted. Runs on its own low-priority
/// thread, cancellable; every folder gets at most 15 s; cloud placeholders (OneDrive "online only") and reparse points are never entered.
/// Sizes are the logical file sizes (the size on disk would cost one more call per file; ponytail: switch to GetCompressedFileSize if people compare with Explorer).
/// </summary>
public static unsafe class DiskScanner
{
    public sealed record Root(string Title, string Path, string Relative);
    public sealed record Result(List<DiskRow> Rows, List<string> NotCounted);

    static readonly TimeSpan PerFolder = TimeSpan.FromSeconds(15);

    /// <summary>The folders to measure, in a stable order. Directory listing only (fast); sizes come from <see cref="ScanAsync"/>.</summary>
    public static List<Root> Plan()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<Root>();
        void Children(string dir, string rel, bool skipAppData)
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = System.IO.Path.GetFileName(d);
                    if (skipAppData && name.Equals("AppData", StringComparison.OrdinalIgnoreCase)) continue;
                    var attrs = File.GetAttributes(d);
                    if ((attrs & FileAttributes.ReparsePoint) != 0 || CheckupNative.IsPlaceholder((uint)attrs)) continue;   // "Application Data" junctions, cloud folders
                    var r = rel.Length == 0 ? name : rel + "\\" + name;
                    roots.Add(new(DiskAdvice.Title(r), d, r));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Children(profile, "", true);
        foreach (var sub in new[] { "Local", "Roaming", "LocalLow" }) Children(System.IO.Path.Combine(profile, "AppData", sub), "AppData\\" + sub, false);

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        void Extra(string title, string path, string rel) { if (Directory.Exists(path)) roots.Add(new(title, path, rel)); }
        Extra(L("Recycle Bin"), System.IO.Path.Combine(System.IO.Path.GetPathRoot(profile) ?? "C:\\", "$Recycle.Bin", WindowsIdentity.GetCurrent().User?.Value ?? "-"), "Recycle Bin");
        Extra(L("Windows Update downloads"), System.IO.Path.Combine(win, "SoftwareDistribution", "Download"), "SoftwareDistribution");
        Extra(L("Delivery Optimization"), System.IO.Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), "DeliveryOptimization");
        return roots;
    }

    /// <summary>Measures every root; progress = (done, total, title of the folder being checked). Top 10 by size, plus the names that could not be measured.</summary>
    public static Task<Result> ScanAsync(IReadOnlyList<Root> roots, IProgress<(int Done, int Total, string Title)> progress, CancellationToken ct) =>
        Task.Factory.StartNew(() =>
        {
            CheckupNative.SetThreadPriority(CheckupNative.GetCurrentThread(), CheckupNative.ThreadModeBackgroundBegin);
            try
            {
                var rows = new List<DiskRow>(); var skipped = new List<string>();
                for (int i = 0; i < roots.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report((i, roots.Count, roots[i].Title));
                    var m = Measure(roots[i].Path, ct);
                    if (m == null) { skipped.Add(roots[i].Title); continue; }
                    if (m.Value.Total > 0) rows.Add(new(roots[i].Title, roots[i].Path, m.Value.Total, m.Value.Children.OrderByDescending(c => c.Bytes).Take(8).ToList(), roots[i].Relative));
                }
                progress.Report((roots.Count, roots.Count, ""));
                return new Result(rows.OrderByDescending(r => r.Bytes).Take(10).ToList(), skipped);
            }
            finally { CheckupNative.SetThreadPriority(CheckupNative.GetCurrentThread(), CheckupNative.ThreadModeBackgroundEnd); }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Total bytes and the totals of the direct sub-folders; null when the folder cannot be opened or takes longer than 15 s.</summary>
    static (long Total, List<(string Name, long Bytes)> Children)? Measure(string root, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + PerFolder;
        long total = 0, files = 0;
        var children = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Dir, string? Top)>();
        stack.Push((root, null));
        bool first = true;
        while (stack.Count > 0)
        {
            var (dir, top) = stack.Pop();
            var h = CheckupNative.FindFirstFileExW(@"\\?\" + dir + @"\*", CheckupNative.FindExInfoBasic, out var d, CheckupNative.FindExSearchNameMatch, IntPtr.Zero, CheckupNative.FindFirstExLargeFetch);
            if (h == new IntPtr(-1)) { if (first) return null; continue; }   // the root itself is closed: "not counted"; deeper folders are just skipped
            first = false;
            try
            {
                do
                {
                    if ((++files & 2047) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (DateTime.UtcNow > deadline) return null;
                    }
                    var name = new string(d.cFileName);
                    if (name is "." or "..") continue;
                    var attrs = d.dwFileAttributes;
                    if ((attrs & CheckupNative.AttrReparsePoint) != 0 || CheckupNative.IsPlaceholder(attrs)) continue;
                    if ((attrs & CheckupNative.AttrDirectory) != 0) { stack.Push((dir + "\\" + name, top ?? name)); continue; }
                    long size = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
                    total += size;
                    if (top != null) children[top] = children.GetValueOrDefault(top) + size;
                } while (CheckupNative.FindNextFileW(h, out d));
            }
            finally { CheckupNative.FindClose(h); }
        }
        return (total, children.Select(kv => (kv.Key, kv.Value)).ToList());
    }
}
