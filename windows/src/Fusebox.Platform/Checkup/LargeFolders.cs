namespace Fusebox.Platform.Checkup;

/// <summary>"Large folders": sums file sizes under a root. On demand only, on its own low-priority thread, cancellable.</summary>
public static unsafe class LargeFolders
{
    public sealed record Row(string Path, long Bytes);

    /// <summary>Top folders (depth 1 and 2 below root) by size. Skips reparse points (junctions, OneDrive placeholders' links).</summary>
    // ponytail: depth 1–2 only, nested rows can repeat their parent's bytes; a real tree view if people ask.
    public static Task<List<Row>> ScanAsync(string root, CancellationToken ct, int top = 15) =>
        Task.Factory.StartNew(() =>
        {
            CheckupNative.SetThreadPriority(CheckupNative.GetCurrentThread(), CheckupNative.ThreadModeBackgroundBegin);
            try
            {
                var rows = new List<Row>();
                Walk(root, 0, rows, ct);
                return rows.OrderByDescending(r => r.Bytes).Take(top).ToList();
            }
            finally { CheckupNative.SetThreadPriority(CheckupNative.GetCurrentThread(), CheckupNative.ThreadModeBackgroundEnd); }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    static long Walk(string dir, int depth, List<Row> rows, CancellationToken ct)
    {
        long total = 0;
        var subs = new List<string>();
        var h = CheckupNative.FindFirstFileExW(@"\\?\" + dir + @"\*", CheckupNative.FindExInfoBasic, out var d, CheckupNative.FindExSearchNameMatch, IntPtr.Zero, CheckupNative.FindFirstExLargeFetch);
        if (h == new IntPtr(-1)) return 0;   // access denied or gone
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var name = new string(d.cFileName);
                if (name is "." or "..") continue;
                if ((d.dwFileAttributes & CheckupNative.AttrReparsePoint) != 0) continue;
                if ((d.dwFileAttributes & CheckupNative.AttrDirectory) != 0) subs.Add(name);
                else total += ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
            } while (CheckupNative.FindNextFileW(h, out d));
        }
        finally { CheckupNative.FindClose(h); }
        foreach (var s in subs)
        {
            var path = Path.Combine(dir, s);
            long size = Walk(path, depth + 1, rows, ct);
            if (depth < 2) rows.Add(new(path, size));
            total += size;
        }
        return total;
    }
}
