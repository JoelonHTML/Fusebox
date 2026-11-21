using Microsoft.Win32;

namespace Fusebox.App;

/// <summary>HKCU\...\Run value "Fusebox" = "exe" --background (no admin needed).</summary>
public static class StartAtLogin
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get { try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Fusebox") != null; } catch { return false; } }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("Fusebox", $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue("Fusebox", false);
        }
        catch { }
    }
}
