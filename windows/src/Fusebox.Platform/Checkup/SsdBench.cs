using System.Diagnostics;
using System.Runtime.InteropServices;
using Fusebox.Core.Checkup;

namespace Fusebox.Platform.Checkup;

/// <summary>
/// SSD speed test (§5.B.4): writes 128 × 8 MB of random bytes (1 GiB) to a temp file with write-through and no OS cache (sector-aligned buffer),
/// flushes, reads it back the same way. The file is always deleted. Needs ≥ 5 GB free; cancellable; progress 0…1 in ≥ 2 % steps.
/// </summary>
public static unsafe class SsdBench
{
    const int Chunk = 8 * 1024 * 1024, Chunks = 128;
    const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <summary>Thrown when the temp drive has less than 5 GB free.</summary>
    public sealed class NotEnoughSpace : Exception { }

    public static Task<SsdBenchResult> RunAsync(Action<double> progress, CancellationToken ct) => Task.Factory.StartNew(() => Run(progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    static SsdBenchResult Run(Action<double> progress, CancellationToken ct)
    {
        var temp = Path.GetTempPath();
        if (new DriveInfo(Path.GetPathRoot(temp)!).AvailableFreeSpace < 5L * 1_000_000_000) throw new NotEnoughSpace();
        var file = Path.Combine(temp, $"fusebox-speedtest-{Environment.ProcessId}.tmp");
        void* buf = NativeMemory.AlignedAlloc(Chunk, 4096);
        try
        {
            Random.Shared.NextBytes(new Span<byte>(buf, Chunk));
            int lastPct = -1;
            void Report(double v) { int p = (int)(v * 50); if (p != lastPct) { lastPct = p; progress(v); } }

            var sw = new Stopwatch();
            using (var h = File.OpenHandle(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.WriteThrough | NoBuffering))
            {
                sw.Start();
                for (int i = 0; i < Chunks; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, new ReadOnlySpan<byte>(buf, Chunk), (long)i * Chunk);
                    Report(i / (Chunks * 2.0));
                }
                RandomAccess.FlushToDisk(h);
                sw.Stop();
            }
            double write = SsdBenchResult.Speed(sw.Elapsed.TotalSeconds);

            sw.Restart();
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan | NoBuffering))
            {
                for (int i = 0; i < Chunks; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (RandomAccess.Read(h, new Span<byte>(buf, Chunk), (long)i * Chunk) != Chunk) throw new IOException("short read");
                    Report(0.5 + i / (Chunks * 2.0));
                }
                sw.Stop();
            }
            return new(SsdBenchResult.Speed(sw.Elapsed.TotalSeconds), write, DateTime.Now);
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
