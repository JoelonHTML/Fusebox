using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Fusebox.Core;
using Fusebox.Core.Update;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Fusebox.App;

/// <summary>
/// Updates from GitHub Releases (win- tags), mirroring the Mac Updater: check 15 s after start and every 15 min (hourly in low impact),
/// verify ECDSA signature + build number, swap files (running files are renamed to *.old), restart.
/// Dev builds (build 0) only show that an update exists; a click on "Check for updates" still installs.
/// </summary>
public static partial class Updater
{
    public enum State { Idle, Checking, UpToDate, Available, Installing, Failed }

    public static State Current { get; private set; } = State.Idle;
    public static string? Tag { get; private set; }
    public static string? Error { get; private set; }
    public static DateTime? LastCheck { get; private set; }
    /// <summary>null = loading, empty = failed (retried next time).</summary>
    public static List<ReleaseInfo>? Changelog { get; private set; }
    public static event Action? Changed;

    public static long CurrentBuild => BuildInfo.Build;
    public static string CurrentVersion => $"{BuildInfo.Version}{(BuildInfo.Version.StartsWith("0.") ? " beta" : "")} ({CurrentBuild})";
    static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    static bool Auto => Prefs.Get("autoUpdate", true);

    static readonly HttpClient Http = Make(TimeSpan.FromSeconds(20));
    static readonly HttpClient Download = Make(TimeSpan.FromMinutes(10));
    static UpdateOffer? latest;
    static DispatcherQueueTimer? timer;

    static HttpClient Make(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Fusebox-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static void Set(State s, string? error = null) { Current = s; Error = error; Changed?.Invoke(); }

    /// <summary>Once at startup (UI thread).</summary>
    public static void Start()
    {
        if (timer != null) return;
        AnnounceIfUpdated();
        var q = DispatcherQueue.GetForCurrentThread();
        var first = q.CreateTimer();
        first.Interval = TimeSpan.FromSeconds(15);
        first.IsRepeating = false;
        first.Tick += (_, _) => _ = Check(false);
        first.Start();
        timer = q.CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(15);
        timer.Tick += (_, _) =>
        {
            if (LowImpact.On && LastCheck is { } l && DateTime.UtcNow - l < TimeSpan.FromMinutes(55)) return;   // low impact: hourly
            _ = Check(false);
        };
        timer.Start();
    }

    public static async Task LoadChangelog()
    {
        if (Changelog is { Count: > 0 }) return;
        Changelog = null;
        Changed?.Invoke();
        try { Changelog = UpdateLogic.Merged(UpdateLogic.ParseReleases(await Http.GetStringAsync($"https://api.github.com/repos/{UpdateLogic.Repo}/releases?per_page=60"))); }
        catch { Changelog = []; }
        Changed?.Invoke();
    }

    public static async Task Check(bool userInitiated)
    {
        if (Current is State.Checking or State.Installing) return;
        Set(State.Checking);
        string? json = null;
        string? why = null;
        try
        {
            using var r = await Http.GetAsync($"https://api.github.com/repos/{UpdateLogic.Repo}/releases?per_page=30");
            if (r.IsSuccessStatusCode) json = await r.Content.ReadAsStringAsync();
            else why = Strings.L("GitHub answered %lld", (int)r.StatusCode);
        }
        catch (Exception e) { why = e.Message; }
        LastCheck = DateTime.UtcNow;
        var offer = UpdateLogic.PickUpdate(json, Arch);
        if (offer == null)
        {
            if (json == null) Set(userInitiated ? State.Failed : State.Idle, userInitiated ? why : null);
            else Set(State.UpToDate);
            return;
        }
        if (offer.Build <= CurrentBuild) { Set(State.UpToDate); return; }
        latest = offer;
        Tag = offer.Tag;
        Set(State.Available);
        if (!userInitiated) NotifyOnce(offer.Tag);
        bool failedBefore = Prefs.Get("failedUpdateBuild", 0L) == offer.Build;   // don't retry a broken update every 15 min
        if (userInitiated || (Auto && !failedBefore && CurrentBuild > 0)) await Install();
    }

    static void NotifyOnce(string tag)
    {
        if (Prefs.Get("notifiedUpdate", "") == tag) return;
        Prefs.Set("notifiedUpdate", tag);
        Notify.Send(Strings.L("Fusebox %@ is available", UpdateLogic.Pretty(tag)),
            Auto && CurrentBuild > 0 ? Strings.L("Installing now. Fusebox restarts in a moment.") : Strings.L("Open Settings › About to install it."));
    }

    /// <summary>First start after an update: one notification with what is new.</summary>
    static async void AnnounceIfUpdated()
    {
        long seen = Prefs.Get("lastSeenBuild", 0L), now = CurrentBuild;
        if (now == 0) return;
        Prefs.Set("lastSeenBuild", now);
        if (now <= seen || !(seen > 0 || Prefs.Get("setupDone", false)) || !Prefs.Get("notify", true)) return;
        try
        {
            var list = UpdateLogic.ParseReleases(await Http.GetStringAsync($"https://api.github.com/repos/{UpdateLogic.Repo}/releases?per_page=10"));
            var mine = list.FirstOrDefault(r => UpdateLogic.BuildNumber(r.Tag) == now) ?? list.FirstOrDefault();
            var version = mine == null ? "" : UpdateLogic.Pretty(mine.Tag);
            var summary = mine == null ? "" : UpdateLogic.Summary(mine.Notes);
            Notify.Send(version.Length == 0 ? Strings.L("Fusebox updated") : Strings.L("Fusebox updated to %@", version),
                summary.Length > 0 ? summary : Strings.L("See what's new in Settings › About."));
        }
        catch { }
    }

    static async Task Install()
    {
        if (latest is not { } o) return;
        Set(State.Installing);
        string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string tmp = Path.Combine(Path.GetTempPath(), "fusebox-update-" + Guid.NewGuid().ToString("N"));
        string staging = appDir + ".new";
        try
        {
            Directory.CreateDirectory(tmp);
            var sig = await Http.GetStringAsync(o.Sig);
            if (sig.Length > 4096) throw new InvalidDataException(Strings.L("Download failed"));
            var zip = Path.Combine(tmp, "update.zip");
            await Fetch(o.Zip, zip);
            string root = await Task.Run(() =>
            {
                using (var f = File.OpenRead(zip))
                    if (!Signing.Verify(f, sig)) throw new InvalidDataException(Strings.L("Update isn't signed by the Fusebox maker"));
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                using (var za = ZipFile.OpenRead(zip))
                {
                    if (za.Entries.Sum(e => e.Length) > 1_000_000_000) throw new InvalidDataException(Strings.L("Not a Fusebox update"));
                    za.ExtractToDirectory(staging);   // rejects paths outside the folder
                }
                var r = Path.Combine(staging, "Fusebox");
                if (!File.Exists(Path.Combine(r, "Fusebox.exe"))) throw new InvalidDataException(Strings.L("Not a Fusebox update"));
                if (BuildOf(r) != o.Build) throw new InvalidDataException(Strings.L("Version mismatch"));
                Swap(appDir, r);
                return r;
            });
            try { Directory.Delete(staging, true); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
            Relaunch(appDir);
        }
        catch (Exception e)
        {
            Prefs.Set("failedUpdateBuild", o.Build);
            try { Directory.Delete(staging, true); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
            Set(State.Failed, e.Message);
        }
    }

    static async Task Fetch(Uri url, string file)
    {
        using var r = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        r.EnsureSuccessStatusCode();
        if (r.Content.Headers.ContentLength > UpdateLogic.MaxZipBytes) throw new InvalidDataException(Strings.L("Download failed"));
        await using var src = await r.Content.ReadAsStreamAsync();
        await using var dst = File.Create(file);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            total += n;
            if (total > UpdateLogic.MaxZipBytes) throw new InvalidDataException(Strings.L("Download failed"));
            await dst.WriteAsync(buf.AsMemory(0, n));
        }
    }

    [GeneratedRegex(@"\+(\d{8,})")]
    private static partial Regex BuildInVersion();

    /// <summary>Build inside the new folder: the exe's product version ("0.1.0+202611212130"), else build.txt.</summary>
    static long BuildOf(string root)
    {
        try
        {
            var m = BuildInVersion().Match(FileVersionInfo.GetVersionInfo(Path.Combine(root, "Fusebox.exe")).ProductVersion ?? "");
            if (m.Success) return long.Parse(m.Groups[1].Value);
            return long.TryParse(File.ReadAllText(Path.Combine(root, "build.txt")).Trim(), out var b) ? b : -1;
        }
        catch { return -1; }
    }

    /// <summary>Running files can be renamed on Windows: old ones become *.old (deleted at the next start), new ones move in. Rolls back on failure.</summary>
    static void Swap(string appDir, string newRoot)
    {
        var renamed = new List<string>();
        var added = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".old")).ToList())
            {
                File.Move(f, f + ".old", true);
                renamed.Add(f);
            }
            foreach (var f in Directory.EnumerateFiles(newRoot, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(appDir, Path.GetRelativePath(newRoot, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(f, dest, true);
                added.Add(dest);
            }
        }
        catch
        {
            foreach (var a in added) try { File.Delete(a); } catch { }
            foreach (var r in renamed) try { File.Move(r + ".old", r, true); } catch { }
            throw;
        }
    }

    static void Relaunch(string appDir)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(appDir, "Fusebox.exe"), $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = appDir });
        Application.Current.Exit();
    }

    /// <summary>Delete leftovers of a finished update (*.old, *.new). Call a while after start.</summary>
    public static void CleanUp()
    {
        try
        {
            var dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            foreach (var f in Directory.EnumerateFiles(dir, "*.old", SearchOption.AllDirectories)) try { File.Delete(f); } catch { }
            if (Directory.Exists(dir + ".new")) Directory.Delete(dir + ".new", true);
        }
        catch { }
    }
}
