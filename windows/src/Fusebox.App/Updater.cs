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
        // A build that failed to install is skipped (unless the user clicks), so one bad release doesn't freeze updates.
        var offer = UpdateLogic.PickUpdate(json, Arch, skipBuild: userInitiated ? 0 : Prefs.Get("failedUpdateBuild", 0L));
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
        if (userInitiated || AutoInstall) await Install();
    }

    static string AppDir => AppContext.BaseDirectory.TrimEnd('\\', '/');
    static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fusebox");
    static string Marker => Path.Combine(DataDir, "update-pending");
    static string OldList => Path.Combine(DataDir, "update-old.txt");

    /// <summary>Automatic installs only for release builds living in the install folder; elsewhere the update is just announced.</summary>
    static bool AutoInstall => Auto && CurrentBuild > 0 && UpdateSafety.IsInstallDir(AppDir, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    static void NotifyOnce(string tag)
    {
        if (Prefs.Get("notifiedUpdate", "") == tag) return;
        Prefs.Set("notifiedUpdate", tag);
        Notify.Send(Strings.L("Fusebox %@ is available", UpdateLogic.Pretty(tag)),
            AutoInstall ? Strings.L("Installing now. Fusebox restarts in a moment.") : Strings.L("Open Settings › About to install it."));
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
        string appDir = AppDir;
        string tmp = Path.Combine(Path.GetTempPath(), "fusebox-update-" + Guid.NewGuid().ToString("N"));
        string staging = appDir + ".new";
        try
        {
            Directory.CreateDirectory(tmp);
            await Fetch(o.Sig, Path.Combine(tmp, "update.sig"), 4096);
            var sig = File.ReadAllText(Path.Combine(tmp, "update.sig"));
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
                // The signature doesn't cover the CPU type: refuse an exe for the other architecture.
                var head = new byte[4096];
                using (var fs = File.OpenRead(Path.Combine(r, "Fusebox.exe"))) head = head[..fs.Read(head)];
                if (!UpdateSafety.MachineMatches(UpdateSafety.PeMachine(head), RuntimeInformation.ProcessArchitecture == Architecture.Arm64))
                    throw new InvalidDataException(Strings.L("Not a Fusebox update"));
                Swap(appDir, r);
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(Marker, o.Build.ToString());   // deleted by Confirm() once this build has run for a while
                return r;
            });
            try { Directory.Delete(staging, true); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
            Relaunch(appDir);
        }
        catch (Exception e)
        {
            if (e is not (HttpRequestException or TaskCanceledException)) Prefs.Set("failedUpdateBuild", o.Build);   // a network hiccup isn't a bad release
            try { Directory.Delete(staging, true); } catch { }
            try { Directory.Delete(tmp, true); } catch { }
            Set(State.Failed, e.Message);
        }
    }

    static async Task Fetch(Uri url, string file, long max = UpdateLogic.MaxZipBytes)
    {
        using var r = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        r.EnsureSuccessStatusCode();
        if (r.Content.Headers.ContentLength > max) throw new InvalidDataException(Strings.L("Download failed"));
        await using var src = await r.Content.ReadAsStreamAsync();
        await using var dst = File.Create(file);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            total += n;
            if (total > max) throw new InvalidDataException(Strings.L("Download failed"));
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

    /// <summary>Running files can be renamed on Windows: only files that the new version also has become *.old, then the new ones move in. Other files stay. Rolls back on failure.</summary>
    static void Swap(string appDir, string newRoot)
    {
        var renamed = new List<string>();   // relative paths whose old copy is now *.old
        var added = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(newRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(newRoot, f);
                var dest = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest)) { File.Move(dest, dest + ".old", true); renamed.Add(rel); }
                File.Move(f, dest, true);
                added.Add(rel);
            }
            Directory.CreateDirectory(DataDir);
            File.WriteAllLines(OldList, renamed);
        }
        catch
        {
            foreach (var a in added) try { File.Delete(Path.Combine(appDir, a)); } catch { }
            foreach (var r in renamed) try { File.Move(Path.Combine(appDir, r) + ".old", Path.Combine(appDir, r), true); } catch { }
            throw;
        }
    }

    /// <summary>Before any UI: the previous update never confirmed it starts and is older than 2 minutes → put the old files back, restart. True when it did (caller exits).</summary>
    public static bool RollBackIfNeverConfirmed()
    {
        try
        {
            if (!File.Exists(Marker)) return false;
            var dir = AppDir;
            var rels = File.Exists(OldList) ? File.ReadAllLines(OldList).Where(l => l.Length > 0 && File.Exists(Path.Combine(dir, l) + ".old")).ToList() : [];
            if (!UpdateSafety.ShouldRollBack(DateTime.UtcNow - File.GetLastWriteTimeUtc(Marker), rels.Count > 0)) return false;
            long.TryParse(File.ReadAllText(Marker).Trim(), out var bad);
            File.Delete(Marker);
            if (bad > 0) Prefs.Set("failedUpdateBuild", bad);
            foreach (var rel in rels)
            {
                var f = Path.Combine(dir, rel);
                if (File.Exists(f)) File.Move(f, f + ".bad", true);   // the running exe can be renamed, not overwritten
                File.Move(f + ".old", f, true);
            }
            Process.Start(new ProcessStartInfo(Path.Combine(dir, "Fusebox.exe"), $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = dir });
            return true;
        }
        catch { return false; }
    }

    static void Relaunch(string appDir)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(appDir, "Fusebox.exe"), $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = appDir });
        Application.Current.Exit();
    }

    /// <summary>The new build has run for a while: that's the proof it starts. Drop the rollback marker, then delete leftovers (*.old/*.bad next to a file that exists, *.new).</summary>
    public static void Confirm()
    {
        try
        {
            File.Delete(Marker);
            var dir = AppDir;
            foreach (var ext in new[] { "*.old", "*.bad" })
                foreach (var f in Directory.EnumerateFiles(dir, ext, SearchOption.AllDirectories))
                    if (File.Exists(f[..^4])) try { File.Delete(f); } catch { }
            File.Delete(OldList);
            if (Directory.Exists(dir + ".new")) Directory.Delete(dir + ".new", true);
        }
        catch { }
    }
}
