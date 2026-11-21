using Fusebox.Core.Checkup;
using Fusebox.Core.Update;

public class UpdateTests
{
    [Fact]
    public void TagsAndPretty()
    {
        Assert.Equal(202611212130, UpdateLogic.BuildNumber("win-v0.1-beta-202611212130"));
        Assert.Null(UpdateLogic.BuildNumber("win-v0.1-beta"));
        Assert.Equal("0.1 beta", UpdateLogic.Pretty("win-v0.1-beta-202611212130"));
        Assert.Equal("1.0", UpdateLogic.Pretty("win-v1.0-202701010000"));
    }

    [Fact]
    public void Summary()
    {
        Assert.Equal("Docked · Much lighter · Fonts", UpdateLogic.Summary("- Docked: see what's plugged in\n- Much lighter — 5% CPU\n- Fonts: six\n- More"));
        Assert.Equal("Plain line", UpdateLogic.Summary("Plain line"));
    }

    const string Base = "https://github.com/JoelonHTML/Fusebox/releases/download/";

    static string Rel(string tag, string url, bool draft = false, bool sig = true) =>
        "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"name\":\"Fusebox for Windows\",\"body\":\"- A\",\"published_at\":\"2026-11-21T10:00:00Z\",\"assets\":["
        + "{\"name\":\"Fusebox-Windows-x64.zip\",\"browser_download_url\":\"" + url + "\"}"
        + (sig ? ",{\"name\":\"Fusebox-Windows-x64.zip.sig\",\"browser_download_url\":\"" + url + ".sig\"}" : "") + "]}";

    [Fact]
    public void PicksNewestValidRelease()
    {
        var json = "[" + string.Join(",",
            Rel("v0.9-202701010000", Base + "v0.9/Fusebox-Windows-x64.zip"),   // Mac tag
            Rel("win-v0.1-beta-202611210900", Base + "win-v0.1-beta-202611210900/Fusebox-Windows-x64.zip"),
            Rel("win-v0.1-beta-202611211200", Base + "win-v0.1-beta-202611211200/Fusebox-Windows-x64.zip"),
            Rel("win-v0.2-beta-202611220000", Base + "x/Fusebox-Windows-x64.zip", draft: true),
            Rel("win-v0.3-beta-202611230000", "http://evil.example/Fusebox-Windows-x64.zip"),
            Rel("win-v0.4-beta-202611240000", "https://github.com/someone/else/releases/download/t/Fusebox-Windows-x64.zip"),
            Rel("win-v0.5-beta-202611250000", Base + "t/Fusebox-Windows-x64.zip", sig: false)) + "]";
        Assert.Equal(202611211200, UpdateLogic.PickUpdate(json, "x64")?.Build);
        Assert.Null(UpdateLogic.PickUpdate(json, "arm64"));
        Assert.Null(UpdateLogic.PickUpdate("garbage", "x64"));
    }

    [Fact]
    public void ChangelogIsWindowsOnlyAndMerged()
    {
        var json = "[" + string.Join(",",
            Rel("v0.9-202701010000", Base + "a"),
            Rel("win-v0.1-beta-202611210900", Base + "a"),
            Rel("win-v0.1-beta-202611211200", Base + "b")) + "]";
        var m = UpdateLogic.Merged(UpdateLogic.ParseReleases(json));
        Assert.Single(m);
        Assert.Equal("win-v0.1-beta-202611211200", m[0].Tag);
        Assert.Equal("- A\n- A", m[0].Notes);
        Assert.Equal(["win-v0.1-beta-202611210900"], m[0].Builds);
    }

    [Fact]
    public void LowImpactRules()
    {
        Assert.True(UpdateLogic.LowImpact("on", false, 100, true));
        Assert.False(UpdateLogic.LowImpact("off", true, 5, false));
        Assert.True(UpdateLogic.LowImpact("auto", true, null, true));
        Assert.True(UpdateLogic.LowImpact(null, false, 19, false));
        Assert.False(UpdateLogic.LowImpact("auto", false, 19, true));
        Assert.False(UpdateLogic.LowImpact("auto", false, 20, false));
        Assert.False(UpdateLogic.LowImpact("auto", false, null, false));
    }

    [Fact]
    public void SignRoundTrip()
    {
        var (priv, pub) = Signing.NewKeyPair();
        var data = "Fusebox.zip"u8.ToArray();
        var sig = Signing.Sign(new MemoryStream(data), priv);
        Assert.True(Signing.Verify(new MemoryStream(data), sig, pub));
        Assert.False(Signing.Verify(new MemoryStream("Fusebox.zip!"u8.ToArray()), sig, pub));
        Assert.False(Signing.Verify(new MemoryStream(data), sig));   // other key
        Assert.False(Signing.Verify(new MemoryStream(data), "not base64", pub));
        Assert.True(Convert.FromBase64String(Signing.ReleaseKey).Length > 60);
    }

    [Fact]
    public void SkipsFailedBuild()
    {
        var json = "[" + string.Join(",",
            Rel("win-v0.1-beta-202611210900", Base + "a/Fusebox-Windows-x64.zip"),
            Rel("win-v0.1-beta-202611211200", Base + "b/Fusebox-Windows-x64.zip")) + "]";
        Assert.Equal(202611211200, UpdateLogic.PickUpdate(json, "x64", skipBuild: 0)?.Build);
        Assert.Equal(202611210900, UpdateLogic.PickUpdate(json, "x64", skipBuild: 202611211200)?.Build);
    }

    static byte[] Pe(ushort machine, int lfanew = 0x80)
    {
        var h = new byte[0x200];
        h[0] = (byte)'M'; h[1] = (byte)'Z';
        BitConverter.GetBytes(lfanew).CopyTo(h, 0x3C);
        h[lfanew] = (byte)'P'; h[lfanew + 1] = (byte)'E';
        BitConverter.GetBytes(machine).CopyTo(h, lfanew + 4);
        return h;
    }

    [Fact]
    public void PeMachineAndArchitecture()
    {
        Assert.Equal(UpdateSafety.MachineX64, UpdateSafety.PeMachine(Pe(0x8664)));
        Assert.Equal(UpdateSafety.MachineArm64, UpdateSafety.PeMachine(Pe(0xAA64, 0x100)));
        Assert.True(UpdateSafety.MachineMatches(UpdateSafety.PeMachine(Pe(0x8664)), arm64: false));
        Assert.False(UpdateSafety.MachineMatches(UpdateSafety.PeMachine(Pe(0xAA64)), arm64: false));
        Assert.False(UpdateSafety.MachineMatches(UpdateSafety.PeMachine(Pe(0x8664)), arm64: true));
        Assert.Null(UpdateSafety.PeMachine("not an exe"u8));
        var bad = Pe(0x8664); BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bad, 0x3C);
        Assert.Null(UpdateSafety.PeMachine(bad));
        Assert.False(UpdateSafety.MachineMatches(null, false));
    }

    [Fact]
    public void RollbackDecision()
    {
        Assert.False(UpdateSafety.ShouldRollBack(null, true));
        Assert.False(UpdateSafety.ShouldRollBack(TimeSpan.FromSeconds(30), true));   // first run of the new build
        Assert.False(UpdateSafety.ShouldRollBack(TimeSpan.FromMinutes(5), false));   // nothing to go back to
        Assert.True(UpdateSafety.ShouldRollBack(TimeSpan.FromMinutes(5), true));
    }

    [Fact]
    public void InstallDir()
    {
        Assert.True(UpdateSafety.IsInstallDir(@"C:\Users\a\AppData\Local\Programs\Fusebox\", @"C:\Users\a\AppData\Local"));
        Assert.False(UpdateSafety.IsInstallDir(@"C:\Users\a\Downloads\Fusebox", @"C:\Users\a\AppData\Local"));
    }

    [Fact]
    public void ElevatedScript()
    {
        Assert.Equal("'C:\\t\\it''s'", ElevatedScripts.Quote("C:\\t\\it's"));
        var s = ElevatedScripts.Script("requests", "C:\\t\\it's\\r.txt")!;
        Assert.StartsWith("$env:PSModulePath = \"$env:SystemRoot\\System32\\WindowsPowerShell\\v1.0\\Modules\"", s);
        Assert.Contains("$env:SystemRoot\\System32\\powercfg.exe", s);
        Assert.EndsWith("Out-File -LiteralPath 'C:\\t\\it''s\\r.txt' -Encoding utf8", s);
        Assert.Null(ElevatedScripts.Script("nope", "x"));
        Assert.Equal("YQBiAA==", ElevatedScripts.Encode("ab"));
    }
}
