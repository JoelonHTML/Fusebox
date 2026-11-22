using Fusebox.Core.Install;

public class AppsListTests
{
    const string L = @"C:\Users\a\AppData\Local";

    [Fact]
    public void OnlyTheInstallFolderCounts()
    {
        Assert.True(AppsList.IsInstalledCopy(@"c:\users\a\appdata\local\Programs\Fusebox\", L));
        Assert.False(AppsList.IsInstalledCopy(@"C:\Users\a\Downloads\Fusebox", L));
        Assert.False(AppsList.IsInstalledCopy(@"C:\Users\a\AppData\Local\Programs\Fusebox\sub", L));
        Assert.True(AppsList.SafeToDelete(AppsList.InstallDir(L), L, true));
        Assert.False(AppsList.SafeToDelete(AppsList.InstallDir(L), L, false));
        Assert.False(AppsList.SafeToDelete(@"C:\Users\a", L, true));
    }

    [Fact]
    public void ValuesAndRewriteRule()
    {
        var v = AppsList.Values(@"C:\x\Fusebox.exe", @"C:\x", "0.2.3 beta", 5_000_000, new DateTime(2026, 11, 22));
        Assert.Equal("20261122", v["InstallDate"]);
        Assert.Equal(4883, v["EstimatedSize"]);
        Assert.Equal("\"C:\\x\\Fusebox.exe\" --uninstall --quiet", v["QuietUninstallString"]);
        Assert.True(AppsList.NeedsWrite(null, null, null, "1", 10, "e"));
        Assert.False(AppsList.NeedsWrite("1", 10, "e", "1", 10, "e"));
        Assert.True(AppsList.NeedsWrite("1", 10, "e", "2", 10, "e"));
        Assert.True(AppsList.NeedsWrite("1", 10, "e", "1", 5000, "e"));
    }
}
