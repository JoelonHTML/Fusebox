using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fusebox.Core.Settings;

public class PrefsTests
{
    [Fact]
    public void DefaultsComeFromTheTable()
    {
        var p = new PrefStore(null);
        Assert.Equal("instrument", p.String("theme"));
        Assert.Equal(1.0, p.Double("textScale"));
        Assert.Equal(30, p.Int("keepDays"));
        Assert.True(p.Bool("keepRunning"));
        Assert.Equal(["cpu", "gpu", "ram", "net", "ssd", "battery"], p.List("metrics"));
        Assert.Equal(90.0, p.Double("alert.cpu.limit"));
        Assert.False(p.Bool("alert.net.on"));
        Assert.Equal("", p.String("netHide"));          // MAC column visible by default (PLAN §7.8)
        Assert.Equal(0x46, p.Int("hotkeyCode"));        // Ctrl+Alt+F
        Assert.Equal(3, p.Int("hotkeyMods"));
    }

    [Fact]
    public void SetFiresChangedOnlyOnRealChange()
    {
        var p = new PrefStore(null);
        var seen = new List<string>();
        p.Changed += seen.Add;
        p.Set("onTop", true);
        p.Set("onTop", true);
        p.Reset("onTop");
        p.Reset("onTop");
        Assert.Equal(["onTop", "onTop"], seen);
        Assert.False(p.Bool("onTop"));
    }

    [Fact]
    public void WrongStoredTypeFallsBackToDefault()
    {
        var p = new PrefStore(null);
        p.Set("keepDays", "lots");
        Assert.Equal(30, p.Int("keepDays"));
    }

    [Fact]
    public void SavesAndReloads()
    {
        var f = Path.Combine(Path.GetTempPath(), $"fusebox-prefs-{Guid.NewGuid():N}.json");
        try
        {
            var a = new PrefStore(f);
            a.Set("theme", "terminal"); a.Set("warnCPU", 70.0); a.Set("keepDays", 7);
            var b = new PrefStore(f);
            Assert.Equal("terminal", b.String("theme"));
            Assert.Equal(70.0, b.Double("warnCPU"));
            Assert.Equal(7, b.Int("keepDays"));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void ExportImportRoundTripSkipsLocalKeys()
    {
        var a = new PrefStore(null);
        a.Set("theme", "terminal"); a.Set("warnCPU", 70.0); a.Set("notify", false); a.Set("pingHost", "evil.example"); a.Set("keepDays", 7);
        var json = a.Export();
        Assert.DoesNotContain("pingHost", json);
        Assert.DoesNotContain("keepDays", json);
        Assert.DoesNotContain("liveSize", json);
        var b = new PrefStore(null);
        Assert.Equal(PrefTable.All.Count(x => x.Scope == PrefScope.Shared), b.Import(json));
        Assert.Equal("terminal", b.String("theme"));
        Assert.Equal(70.0, b.Double("warnCPU"));
        Assert.False(b.Bool("notify"));
    }

    [Fact]
    public void ImportIsATrustBoundary()
    {
        var b = new PrefStore(null);
        Assert.Equal(0, b.Import("""{"warnCPU":"x","theme":3}"""));
        Assert.Equal(0, b.Import("""{"notify":1,"keepDays":2.5,"nope":"x","theme":"%"}""".Replace("%", new string('x', 201))));
        Assert.Equal(0, b.Import("""{"restartDays":2.5,"pingHost":"evil.example","keepDays":0,"autoUpdate":false,"setupDone":true}"""));
        Assert.Equal("1.1.1.1", b.String("pingHost"));
        Assert.Equal(1, b.Import("""{"pingHost":"evil.example","theme":"minimal"}"""));
        Assert.Equal("minimal", b.String("theme"));
        Assert.Equal(2, b.Import("""{"restartDays":30.0,"warnTemp":90}"""));   // integral double for an int; int for a double
        Assert.Equal(30, b.Int("restartDays"));
        Assert.Null(b.Import("[1,2]"));
        Assert.Null(b.Import("not json"));
        Assert.Null(b.Import("{\"theme\":\"" + new string('x', PrefStore.MaxImportBytes) + "\"}"));
    }

    [Fact]
    public void ImportNeverHidesTrayAndTaskbarButton()
    {
        var b = new PrefStore(null);
        b.Import("""{"dockIcon":false,"menubarStyle":"off"}""");
        Assert.True(b.Bool("dockIcon"));
        Assert.Equal("off", b.String("menubarStyle"));
    }

    [Fact]
    public void ProfilesResetEveryTouchedKeyFirst()
    {
        var b = new PrefStore(null);
        foreach (var p in Profile.All)
        {
            Assert.All(p.Values.Keys, k => Assert.True(PrefTable.Known(k), k));
            Assert.DoesNotContain("keepDays", p.Values.Keys);
            Assert.DoesNotContain("pingHost", p.Values.Keys);
        }
        b.Set("pingHost", "nas.local");
        b.ApplyProfile("gamer");
        Assert.Equal("cpu,gpu", b.String("menubarMetrics"));
        Assert.Equal(60.0, b.Double("warnPing"));
        b.ApplyProfile("simple");
        Assert.Equal("cpu", b.String("menubarMetrics"));   // gamer's value reset to default
        Assert.Equal(100.0, b.Double("warnPing"));
        Assert.Equal("liquid", b.String("theme"));
        Assert.Equal(1.15, b.Double("textScale"));
        Assert.Equal("nas.local", b.String("pingHost"));
        b.ApplyProfile("laptop");
        Assert.True(b.Bool("alert.battery.on"));
    }

    [Fact]
    public void ResetAllKeepsSetupDoneAndBookkeeping()
    {
        var b = new PrefStore(null);
        b.Set("setupDone", true); b.Set("theme", "terminal"); b.Set("keepDays", 7); b.Set("liveSize", "400,500");
        b.ResetAll();
        Assert.True(b.Bool("setupDone"));
        Assert.Equal("instrument", b.String("theme"));
        Assert.Equal(30, b.Int("keepDays"));
        Assert.Equal("400,500", b.String("liveSize"));
    }

    [Fact]
    public void MigratesV1Keys()
    {
        var d = JsonNode.Parse("""
            {"language":"system","appearance":"dark","theme":"glass","font":"segoe","lowImpact":true,"lowImpactMode":"on",
             "logDays":90,"tray":false,"hotkey":false,"windowRect":"10,20,1100,720","metrics":"cpu,ram,bat","metricsHidden":"gpu,bat"}
            """)!.AsObject();
        Assert.True(PrefStore.Migrate(d));
        var j = d.ToJsonString();
        foreach (var gone in new[] { "\"language\"", "\"appearance\"", "logDays", "lowImpactMode", "\"tray\"", "\"hotkey\"", "windowRect" })
            Assert.DoesNotContain(gone, j);
        Assert.Equal("", (string?)d["appLanguage"]);
        Assert.Equal("dark", (string?)d["appearanceMode"]);
        Assert.Equal("glas", (string?)d["theme"]);
        Assert.Equal("sfPro", (string?)d["font"]);
        Assert.Equal("on", (string?)d["lowImpact"]);
        Assert.Equal(90, (int?)d["keepDays"]);
        Assert.Equal("off", (string?)d["menubarStyle"]);
        Assert.Equal(-1, (long?)d["hotkeyCode"]);
        Assert.Equal("10,20", (string?)d["windowTopLeft"]);
        Assert.Equal("cpu,ram,battery", (string?)d["metrics"]);
        Assert.Equal("gpu,battery", (string?)d["metricsHidden"]);
        Assert.False(PrefStore.Migrate(d));   // idempotent

        var only = JsonNode.Parse("""{"lowImpact":false,"theme":"blocks","language":"nl"}""")!.AsObject();
        PrefStore.Migrate(only);
        Assert.Null(only["lowImpact"]);        // the v1 live state is not a choice: default auto
        Assert.Equal("blokken", (string?)only["theme"]);
        Assert.Equal("nl", (string?)only["appLanguage"]);
    }

    /// <summary>Every literal key used as Prefs.X("key") in the app exists in the table (typos would silently read defaults).</summary>
    [Fact]
    public void EveryKeyUsedInTheAppIsInTheTable()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "Fusebox.App"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var re = new Regex(@"Prefs\.(?:Get|Set|SetList|Bool|Int|Long|Double|Str|List|Reset)(?:<\w+>)?\(\s*""([^""]+)""");
        var unknown = Directory.EnumerateFiles(Path.Combine(dir!, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => re.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value)))
            .Where(x => !PrefTable.Known(x.Key)).ToList();
        Assert.Empty(unknown);
    }
}
