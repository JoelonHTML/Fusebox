using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fusebox.Core.Checkup;

public enum StartupKind { Run, RunOnce, Folder, Task, Service }

/// <summary>Where an item lives; decides how it is switched (see <see cref="StartupItem.CanToggleDirect"/>).</summary>
public enum StartupScope { User, Machine, Machine32 }

/// <summary>
/// One start-with-Windows item (§5.B.1 on Windows). <see cref="Id"/> is what the switch needs: the registry value name, the file name in
/// the Startup folder, "\Folder\TaskName" for a task, the service name for a service.
/// </summary>
public sealed class StartupItem
{
    public string Id = "", Raw = "", Name = "", Command = "";
    public StartupKind Kind;
    public StartupScope Scope;
    public bool Enabled = true;
    public string? Exe;                       // expanded program path, when the command names one
    public bool Orphan;                       // the program is gone
    public bool Running;
    public double Cpu = double.NaN;           // % of the whole PC, averaged since the process started
    public double MemMb = double.NaN;
    /// <summary>The items a merged row stands for (the switch applies to all of them).</summary>
    public List<StartupItem> Members = [];

    /// <summary>"Apps that start at login" (Run, RunOnce, Startup folders, logon tasks) vs "Background helpers" (auto-start services).</summary>
    public bool AtLogin => Kind != StartupKind.Service;
    /// <summary>Per-user registry/folder items: Fusebox flips StartupApproved in HKCU itself (the switch Task Manager uses).</summary>
    public bool CanToggleDirect => Scope == StartupScope.User && Kind is StartupKind.Run or StartupKind.Folder;
    /// <summary>Everything else that can be switched goes through the elevated PowerShell task.</summary>
    public bool CanToggleElevated => !CanToggleDirect && Kind != StartupKind.RunOnce;
    public bool CanToggle => CanToggleDirect || CanToggleElevated;
}

public static partial class StartupParse
{
    /// <summary>The program a command line starts, with variables expanded: "\"C:\\x y\\a.exe\" /min" → C:\x y\a.exe. Null when none is named.</summary>
    public static string? ExePath(string command, Func<string, string>? expand = null)
    {
        var c = (expand ?? Environment.ExpandEnvironmentVariables)(command.Trim());
        if (c.Length == 0) return null;
        if (c[0] == '"') { int e = c.IndexOf('"', 1); return e > 1 ? c[1..e] : null; }
        // Unquoted: up to ".exe" (paths with spaces), else the first word.
        var m = ExeEnd().Match(c);
        if (m.Success) return c[..(m.Index + m.Length)];
        int sp = c.IndexOf(' ');
        return sp > 0 ? c[..sp] : c;
    }

    [GeneratedRegex(@"\.(exe|com|bat|cmd|scr)\b", RegexOptions.IgnoreCase)] private static partial Regex ExeEnd();
    [GeneratedRegex(@"[_-]?[0-9A-Fa-f]{12,}$")] private static partial Regex HashSuffix();
    [GeneratedRegex(@"^\{?[0-9A-Fa-f]{8}-")] private static partial Regex GuidLike();

    /// <summary>Orphan: an absolute local path (not UNC, not a bare name) that does not exist. Bare names like "cmd" or "rundll32" never are.</summary>
    public static bool IsOrphan(string? exe, Func<string, bool> exists)
    {
        if (string.IsNullOrEmpty(exe) || exe.StartsWith(@"\\") || exe.Length < 3 || exe[1] != ':') return false;
        return !exists(exe);
    }

    /// <summary>Product name when known, else the value name without a machine-generated suffix ("MicrosoftEdgeAutoLaunch_5E0F…" → "MicrosoftEdgeAutoLaunch"), else the exe name.</summary>
    public static string Friendly(string raw, string? exe, string? product)
    {
        if (!string.IsNullOrWhiteSpace(product)) return product.Trim();
        var n = HashSuffix().Replace(raw, "").Trim();
        if (n.Length == 0 || GuidLike().IsMatch(n))
            n = exe != null ? FileStem(exe) : raw;
        return n;
    }

    /// <summary>File name without extension, for either kind of path separator.</summary>
    public static string FileStem(string path)
    {
        var f = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        int dot = f.LastIndexOf('.');
        return dot > 0 ? f[..dot] : f;
    }

    // ---- usage text (§5.B.1) ----

    /// <summary>"0.4% CPU · 52 MB" (no watt part on Windows). "" when nothing is measured.</summary>
    public static string Usage(double cpu, double memMb)
    {
        var parts = new List<string>();
        if (double.IsFinite(cpu)) parts.Add((cpu < 0.1 ? "<0.1" : cpu < 10 ? Fmt.One(cpu) : Math.Round(cpu).ToString(CsvLog.Culture)) + "% CPU");
        if (double.IsFinite(memMb)) parts.Add(memMb >= 1024 ? Fmt.One(memMb / 1024) + " GB" : Math.Round(memMb).ToString(CsvLog.Culture) + " MB");
        return string.Join(" · ", parts);
    }

    /// <summary>Warn colour: ≥ 5 % CPU.</summary>
    public static bool Heavy(double cpu) => cpu >= 5;

    // ---- grouping ----

    /// <summary>Items with the same friendly name become one row (per-user one wins, running if any runs, usage summed); sorted by CPU descending (unmeasured last), then name.</summary>
    public static List<StartupItem> Merge(IEnumerable<StartupItem> items) =>
        items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var rep = g.OrderBy(i => i.Scope == StartupScope.User ? 0 : 1).First();
            var cpus = g.Select(i => i.Cpu).Where(double.IsFinite).ToList();
            var mems = g.Select(i => i.MemMb).Where(double.IsFinite).ToList();
            return new StartupItem
            {
                Id = rep.Id, Raw = rep.Raw, Name = rep.Name, Command = rep.Command, Kind = rep.Kind, Scope = rep.Scope, Exe = rep.Exe,
                Enabled = g.Any(i => i.Enabled), Orphan = g.All(i => i.Orphan), Running = g.Any(i => i.Running),
                Cpu = cpus.Count > 0 ? cpus.Sum() : double.NaN, MemMb = mems.Count > 0 ? mems.Sum() : double.NaN, Members = g.ToList(),
            };
        }).OrderByDescending(i => double.IsFinite(i.Cpu) ? i.Cpu : -1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Distinct app names that start at login and are not orphans and are switched on (the headline number).</summary>
    public static int CountAtLogin(IEnumerable<StartupItem> items) =>
        items.Where(i => i.AtLogin && !i.Orphan && i.Enabled).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    // ---- Task Scheduler ----

    public sealed record TaskInfo(string Path, string Name, string Command, bool Enabled);

    /// <summary>
    /// Logon tasks from <c>schtasks /query /xml</c> (locale-independent XML). Works on the "one document" form and on a stream of
    /// separate &lt;Task&gt; documents. Only enabled-or-disabled tasks with a LogonTrigger and an Exec action, outside \Microsoft\.
    /// </summary>
    public static List<TaskInfo> ParseTasks(string xml)
    {
        var list = new List<TaskInfo>();
        foreach (Match m in TaskBlock().Matches(xml))
        {
            XElement? t;
            try { t = XDocument.Parse(m.Value).Root; } catch (System.Xml.XmlException) { continue; }
            if (t == null) continue;
            XNamespace ns = t.Name.Namespace;
            var uri = t.Element(ns + "RegistrationInfo")?.Element(ns + "URI")?.Value ?? "";
            if (uri.Length == 0 || uri.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
            var triggers = t.Element(ns + "Triggers")?.Elements(ns + "LogonTrigger").ToList() ?? [];
            if (triggers.Count == 0) continue;
            var exec = t.Element(ns + "Actions")?.Elements(ns + "Exec").FirstOrDefault();
            var cmd = exec?.Element(ns + "Command")?.Value;
            if (string.IsNullOrWhiteSpace(cmd)) continue;
            bool enabled = !string.Equals(t.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase)
                           && triggers.Any(x => !string.Equals(x.Element(ns + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase));
            int slash = uri.LastIndexOf('\\');
            list.Add(new(uri, uri[(slash + 1)..], cmd.Trim(), enabled));
        }
        return list;
    }

    [GeneratedRegex(@"<Task\b.*?</Task>", RegexOptions.Singleline)] private static partial Regex TaskBlock();
}
