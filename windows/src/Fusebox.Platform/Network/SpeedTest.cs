using System.Diagnostics;
using System.Net;
using Fusebox.Core.Network;
using static Fusebox.Core.Strings;

namespace Fusebox.Platform.Network;

public sealed record SpeedResult(SpeedKind Kind, double Down, double Up, double? Ping, double? DownPeak, double? UpPeak, double? LoadedPing, double? Variation, double? Jitter);

/// <summary>Uploads a block of zeros, reporting every written byte (HttpContent subclass: AOT-safe, no reflection).</summary>
sealed class CountingContent(int size, Action<int> counted) : HttpContent
{
    static readonly byte[] Chunk = new byte[64 * 1024];
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await SerializeToStreamAsync(stream, context, default);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        for (int left = size; left > 0; left -= Chunk.Length)
        {
            int n = Math.Min(left, Chunk.Length);
            await stream.WriteAsync(Chunk.AsMemory(0, n), ct);
            counted(n);
        }
    }
    protected override bool TryComputeLength(out long length) { length = size; return true; }
}

/// <summary>Cloudflare speed test, same method as the Mac: N parallel streams (each its own connection), timed, per-second samples. Only on request.</summary>
public static class SpeedTest
{
    const string Host = "https://speed.cloudflare.com";
    const int DownChunk = 50_000_000, UpChunk = 10_000_000;   // Cloudflare refuses >= 100 MB per request

    static HttpClient NewClient(int timeoutSec) =>
        new(new SocketsHttpHandler { MaxConnectionsPerServer = 1, UseCookies = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromSeconds(timeoutSec) };

    /// <summary>A refused request in plain words (shown as is): 429 = Cloudflare's own per-connection limit, not something Fusebox can avoid.</summary>
    static void Check(HttpResponseMessage r)
    {
        if (r.IsSuccessStatusCode) return;
        if ((int)r.StatusCode == 429)
        {
            var wait = r.Headers.RetryAfter?.Delta ?? (r.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            int min = wait is { } w && w > TimeSpan.Zero ? Math.Max(1, (int)Math.Ceiling(w.TotalMinutes)) : 5;
            throw new HttpRequestException(L("Cloudflare is limiting speed tests from your connection right now. Try again in %lld min.", min), null, r.StatusCode);
        }
        throw new HttpRequestException(L("Cloudflare's speed test didn't accept the test (error %lld). Try again later.", (int)r.StatusCode), null, r.StatusCode);
    }

    /// <summary>One empty request in ms.</summary>
    static async Task<double> Ping(HttpClient c, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var r = await c.GetAsync($"{Host}/__down?bytes=0", ct);
        await r.Content.ReadAsByteArrayAsync(ct);
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>report(phase, liveMbps, progress 0..1). Throws on failure; OperationCanceledException when cancelled.</summary>
    public static async Task<SpeedResult> RunAsync(SpeedKind kind, Action<string, double?, double> report, CancellationToken ct)
    {
        using var probe = NewClient(5);
        report(L("Measuring delay…"), null, 0);
        var idle = new List<double>();
        for (int i = 0; i < 6; i++) { var ms = await Ping(probe, ct); if (i > 0) idle.Add(ms); }

        var down = await Transfer(kind, false, probe, report, ct);
        var up = await Transfer(kind, true, probe, report, ct);
        var d = SpeedMath.Trimmed(down.Samples, SpeedMath.Skip(kind));
        var u = SpeedMath.Trimmed(up.Samples, SpeedMath.Skip(kind));
        bool full = kind == SpeedKind.Full;
        var jitter = idle.Count > 1 ? (double?)Enumerable.Range(1, idle.Count - 1).Average(i => Math.Abs(idle[i] - idle[i - 1])) : null;
        return new SpeedResult(kind,
            (full ? SpeedMath.Median(d) : SpeedMath.Mean(d)) ?? 0, (full ? SpeedMath.Median(u) : SpeedMath.Mean(u)) ?? 0, SpeedMath.Median(idle),
            full ? d.Max() : null, full ? u.Max() : null,
            full ? new[] { SpeedMath.Median(down.Pings), SpeedMath.Median(up.Pings) }.Where(x => x != null).Max() : null,
            full ? new[] { SpeedMath.Variation(d), SpeedMath.Variation(u) }.Where(x => x != null).Max() : null, jitter);
    }

    static async Task<(List<double> Samples, List<double> Pings)> Transfer(SpeedKind kind, bool upload, HttpClient probe, Action<string, double?, double> report, CancellationToken ct)
    {
        int streams = SpeedMath.Streams(kind);
        double secs = SpeedMath.Seconds(kind);
        long cap = SpeedMath.Cap(kind), total = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? error = null;
        var clients = Enumerable.Range(0, streams).Select(_ => NewClient(15)).ToArray();
        var start = Stopwatch.StartNew();
        void Add(long n) { if (Interlocked.Add(ref total, n) >= cap) stop.Cancel(); }

        async Task Worker(HttpClient c)
        {
            try
            {
                var buf = new byte[64 * 1024];
                while (!stop.IsCancellationRequested && start.Elapsed.TotalSeconds < secs)
                {
                    if (upload)
                    {
                        using var content = new CountingContent(UpChunk, n => Add(n));
                        using var r = await c.PostAsync($"{Host}/__up", content, stop.Token);
                        Check(r);
                    }
                    else
                    {
                        using var r = await c.GetAsync($"{Host}/__down?bytes={DownChunk}", HttpCompletionOption.ResponseHeadersRead, stop.Token);
                        Check(r);
                        await using var s = await r.Content.ReadAsStreamAsync(stop.Token);
                        int n;
                        while ((n = await s.ReadAsync(buf, stop.Token)) > 0) Add(n);
                    }
                }
            }
            catch (Exception e) when (!stop.IsCancellationRequested) { error ??= e; }
            catch (OperationCanceledException) { }
        }

        var pinger = kind == SpeedKind.Full ? Task.Run(async () =>
        {
            var o = new List<double>();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(SpeedMath.Skip(kind)), stop.Token);
                while (!stop.IsCancellationRequested) { o.Add(await Ping(probe, stop.Token)); await Task.Delay(400, stop.Token); }
            }
            catch { }
            return o;
        }) : Task.FromResult(new List<double>());

        var tasks = clients.Select(Worker).ToArray();
        var samples = new List<double>();
        long lastBytes = 0;
        var lastT = TimeSpan.Zero;
        try
        {
            while (true)
            {
                await Task.Delay(250, ct);   // look often (cap, end), measure per second
                var now = start.Elapsed;
                bool done = stop.IsCancellationRequested || now.TotalSeconds >= secs || tasks.All(t => t.IsCompleted);
                var dt = (now - lastT).TotalSeconds;
                if (dt >= 1 || (done && dt >= 0.3))
                {
                    long b = Interlocked.Read(ref total);
                    samples.Add(SpeedMath.Mbps(b - lastBytes, dt));
                    lastBytes = b; lastT = now;
                }
                var name = upload ? L("Upload") : L("Download");
                var title = kind == SpeedKind.Quick ? L("Quick") : L("Full");
                report(L("%@ · %@ · %lld connections · %lld s left", title, name, streams, Math.Max(0, (int)Math.Ceiling(secs - now.TotalSeconds))),
                    samples.Count > 0 ? samples[^1] : null, (upload ? 0.5 : 0) + 0.5 * Math.Min(1, now.TotalSeconds / secs));
                if (done) break;
            }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(tasks); } catch { }
            foreach (var c in clients) c.Dispose();
        }
        var pings = await pinger;
        if (samples.Count == 0 || Interlocked.Read(ref total) < 1_000_000) throw error ?? new HttpRequestException(L("No answer"));
        return (samples, pings);
    }

    /// <summary>Public IP, provider and place from Cloudflare's /meta (only after a click).</summary>
    public static async Task<PublicIp?> MetaAsync(CancellationToken ct)
    {
        using var c = NewClient(8);
        return PublicIpParser.Parse(await c.GetStringAsync($"{Host}/meta", ct));
    }
}
