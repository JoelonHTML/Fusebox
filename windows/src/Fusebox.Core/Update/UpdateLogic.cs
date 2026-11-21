using System.Globalization;
using System.Text.Json.Nodes;

namespace Fusebox.Core.Update;

public sealed record ReleaseInfo(string Tag, string Title, DateTimeOffset? Date, string Notes, List<string> Builds);
public sealed record UpdateOffer(string Tag, long Build, Uri Zip, Uri Sig);

/// <summary>Port of the Mac Updater's pure parts (pretty, buildNumber, summary, merged, parseRelease) for win- releases.</summary>
public static class UpdateLogic
{
    public const string Repo = "JoelonHTML/Fusebox";
    public const string TagPrefix = "win-v";
    public const long MaxZipBytes = 300_000_000;

    /// <summary>Last number of 8+ digits in the tag ("win-v0.1-beta-202611212130").</summary>
    public static long? BuildNumber(string tag)
    {
        var last = System.Text.RegularExpressions.Regex.Matches(tag, "[0-9]+").LastOrDefault()?.Value;
        return last is { Length: >= 8 } && long.TryParse(last, out var n) ? n : null;
    }

    /// <summary>"win-v0.1-beta-202611212130" → "0.1 beta"</summary>
    public static string Pretty(string tag)
    {
        var t = tag.StartsWith("win-") ? tag[4..] : tag;
        var parts = t.TrimStart('v').Split('-').ToList();
        if (parts.Count > 1 && BuildNumber(parts[^1]) != null) parts.RemoveAt(parts.Count - 1);
        return string.Join(' ', parts);
    }

    /// <summary>Release notes → a few words: first three bullets, each cut at ":" or " — ", joined with " · ".</summary>
    public static string Summary(string notes)
    {
        var lines = notes.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var bullets = lines.Where(l => l.StartsWith("- ") || l.StartsWith("* ")).Select(l => l[2..]).ToList();
        if (bullets.Count == 0) { var f = lines.FirstOrDefault() ?? ""; return f.Length > 110 ? f[..110] : f; }
        var s = string.Join(" · ", bullets.Take(3).Select(b =>
        {
            int c = b.IndexOf(':'), d = b.IndexOf(" — ", StringComparison.Ordinal);
            int cut = c >= 0 ? c : d >= 0 ? d : b.Length;
            return b[..cut].Trim();
        }));
        return s.Length > 110 ? s[..107] + "…" : s;
    }

    static JsonArray? Array(string? json)
    {
        try { return json is { Length: < 4_000_000 } ? JsonNode.Parse(json) as JsonArray : null; } catch { return null; }
    }

    /// <summary>Non-draft releases with a win- tag (the Mac's are ignored).</summary>
    public static List<ReleaseInfo> ParseReleases(string? json)
    {
        var list = new List<ReleaseInfo>();
        if (Array(json) is not { } a) return list;
        foreach (var n in a)
        {
            if (n is not JsonObject j || j["tag_name"]?.GetValue<string>() is not { } tag || !tag.StartsWith(TagPrefix)) continue;
            if (j["draft"]?.GetValue<bool>() == true) continue;
            var name = j["name"]?.GetValue<string>();
            DateTimeOffset? date = DateTimeOffset.TryParse(j["published_at"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            list.Add(new(tag, string.IsNullOrEmpty(name) ? $"Fusebox {Pretty(tag)}" : name, date, (j["body"]?.GetValue<string>() ?? "").Trim(), []));
        }
        return list;
    }

    /// <summary>Same version published several times: one line, newest first, notes joined.</summary>
    public static List<ReleaseInfo> Merged(IEnumerable<ReleaseInfo> list)
    {
        var o = new List<ReleaseInfo>();
        foreach (var r in list.OrderByDescending(r => BuildNumber(r.Tag) ?? 0))
        {
            if (o.Count > 0 && o[^1].Title == r.Title)
            {
                var last = o[^1];
                o[^1] = last with { Notes = string.Join("\n", new[] { last.Notes, r.Notes }.Where(s => s.Length > 0)), Builds = [.. last.Builds, r.Tag] };
            }
            else o.Add(r);
        }
        return o;
    }

    /// <summary>Newest non-draft win- release (not <c>skipBuild</c>, a build that failed to install) that has Fusebox-Windows-&lt;arch&gt;.zip and .sig, downloadable only from this repo.</summary>
    public static UpdateOffer? PickUpdate(string? json, string arch, string repo = Repo, long skipBuild = 0)
    {
        if (Array(json) is not { } a) return null;
        UpdateOffer? best = null;
        var zipName = $"Fusebox-Windows-{arch}.zip";
        foreach (var n in a)
        {
            if (n is not JsonObject j || j["tag_name"]?.GetValue<string>() is not { } tag || !tag.StartsWith(TagPrefix)) continue;
            if (j["draft"]?.GetValue<bool>() == true || BuildNumber(tag) is not { } build) continue;
            if (build == skipBuild || (best != null && build <= best.Build)) continue;
            if (j["assets"] is not JsonArray assets) continue;
            var zip = Asset(assets, zipName, repo);
            var sig = Asset(assets, zipName + ".sig", repo);
            if (zip != null && sig != null) best = new(tag, build, zip, sig);
        }
        return best;
    }

    static Uri? Asset(JsonArray assets, string name, string repo)
    {
        foreach (var a in assets)
            if (a?["name"]?.GetValue<string>() == name && Uri.TryCreate(a["browser_download_url"]?.GetValue<string>(), UriKind.Absolute, out var u)
                && u.Scheme == "https" && u.Host == "github.com" && u.AbsolutePath.StartsWith($"/{repo}/releases/download/", StringComparison.Ordinal))
                return u;
        return null;
    }

    /// <summary>Low impact: "on", "off" or (default) automatic = Energy saver, or on battery below 20%.</summary>
    public static bool LowImpact(string? setting, bool energySaver, double? battery, bool charging) => setting switch
    {
        "on" => true,
        "off" => false,
        _ => energySaver || (!charging && (battery ?? 100) < 20),
    };
}
