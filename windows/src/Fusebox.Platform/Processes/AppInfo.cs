using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Fusebox.Core.Checkup;
using Fusebox.Platform.Checkup;
using Microsoft.Win32;

namespace Fusebox.Platform;

/// <summary>What the app page's "About" card shows about an exe (FEATURES.md §4.11.4). Anything unknown stays null: the page leaves the row out.</summary>
public sealed record AppFacts(string? Version, string? Machine, bool Emulated, long? SizeBytes, bool SizeIsMinimum, string? Location, string? SignedBy, DateTime? Installed, bool OpensAtLogin);

/// <summary>Reads <see cref="AppFacts"/>. Slow (folder walk, signature check): call on a worker thread, once per path.</summary>
public static unsafe partial class AppInfo
{
    public static AppFacts Read(string exe, string displayName)
    {
        string? version = null, machine = null, signer = null, location = null;
        bool emulated = false;
        try { version = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion; } catch { }
        try { location = Path.GetDirectoryName(exe); } catch { }
        try { (machine, emulated) = MachineOf(exe); } catch { }
        try { signer = SignedBy(exe); } catch { }
        long? size = null;
        bool min = false;
        var folder = InstallFolder(exe);
        if (folder != null) { try { (size, min) = FolderSize(folder); } catch { } }
        DateTime? installed = null;
        try { installed = InstalledOn(exe, folder); } catch { }
        bool login = false;
        try { login = OpensAtLogin(exe, displayName); } catch { }
        return new(string.IsNullOrWhiteSpace(version) ? null : version.Trim(), machine, emulated, size, min, location, signer, installed, login);
    }

    /// <summary>The PE machine type of the file: ("x64" | "Arm64" | "x86", runs under emulation on this PC).</summary>
    public static (string? Machine, bool Emulated) MachineOf(string exe)
    {
        using var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> head = stackalloc byte[64];
        if (f.Read(head) < 64 || head[0] != 'M' || head[1] != 'Z') return (null, false);
        int pe = BitConverter.ToInt32(head[60..]);
        if (pe <= 0 || pe > 1 << 20) return (null, false);
        f.Position = pe;
        Span<byte> sig = stackalloc byte[6];
        if (f.Read(sig) < 6 || sig[0] != 'P' || sig[1] != 'E') return (null, false);
        bool arm = RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        return BitConverter.ToUInt16(sig[4..]) switch
        {
            0x8664 => ("x64", arm),
            0xAA64 => ("Arm64", false),
            0x14C => ("x86", arm),
            _ => (null, false),
        };
    }

    /// <summary>The folder that belongs to the app: its own folder, or the app's folder under Program Files / WindowsApps. Null for Windows itself and shared roots.</summary>
    public static string? InstallFolder(string exe)
    {
        var dir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir)) return null;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (dir.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") })
        {
            if (root.Length == 0 || !dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var first = dir[(root.Length + 1)..].Split(Path.DirectorySeparatorChar)[0];
            return Path.Combine(root, first);
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (dir.TrimEnd('\\').Equals(profile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || Path.GetPathRoot(dir)?.TrimEnd('\\') == dir.TrimEnd('\\')) return null;
        return dir;
    }

    // ponytail: capped at 30 000 files / 2 s; the page then says "at least". Upgrade path: a real allocated-size walk in the background.
    static (long Bytes, bool Minimum) FolderSize(string dir)
    {
        long total = 0;
        int n = 0;
        var end = Environment.TickCount64 + 2000;
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opt))
        {
            total += f.Length;
            if (++n >= 30_000 || (n % 256 == 0 && Environment.TickCount64 > end)) return (total, true);
        }
        return (total, false);
    }

    static DateTime? InstalledOn(string exe, string? folder)
    {
        foreach (var (hive, sub) in new[] { (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"), (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                                            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall") })
        {
            using var root = hive.OpenSubKey(sub);
            if (root == null) continue;
            foreach (var k in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(k);
                if (key?.GetValue("InstallDate") is not string d || d.Length != 8) continue;
                var loc = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"').TrimEnd('\\');
                var icon = key.GetValue("DisplayIcon") as string;
                bool same = (loc is { Length: > 3 } && folder != null && loc.Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            || (icon != null && icon.Contains(exe, StringComparison.OrdinalIgnoreCase));
                if (same && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date)) return date;
            }
        }
        return null;
    }

    /// <summary>Run key / Startup folder entry that points at this exe (or is named like the app), enabled in Startup apps.</summary>
    static bool OpensAtLogin(string exe, string displayName)
    {
        string stem = Path.GetFileNameWithoutExtension(exe);
        // Same source as Checkup › Startup items (Run keys, Startup folders, logon tasks, services).
        foreach (var it in Fusebox.Platform.Checkup.StartupScanner.Scan())
        {
            if (!it.Enabled) continue;
            bool folder = it.Kind == Fusebox.Core.Checkup.StartupKind.Folder;
            if (folder ? it.Name.Equals(displayName, StringComparison.CurrentCultureIgnoreCase) || it.Name.Equals(stem, StringComparison.CurrentCultureIgnoreCase)
                       : (it.Exe ?? it.Command).Contains(exe, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ---- Authenticode: WinVerifyTrust says whether the embedded signature is valid, the certificate gives the name ----

    /// <summary>The signer's name when the file has a valid embedded signature, else null (catalog-signed Windows files and unsigned files both give null).</summary>
    public static string? SignedBy(string file)
    {
        fixed (char* path = file)
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = (IntPtr)path };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA), dwUIChoice = 2 /* none */, fdwRevocationChecks = 0, dwUnionChoice = 1 /* file */,
                pFile = (IntPtr)(&fileInfo), dwStateAction = 0, dwProvFlags = 0x00000010 /* WTD_CACHE_ONLY_URL_RETRIEVAL */,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
            if (WinVerifyTrust(-1, ref action, ref data) != 0) return null;
        }
#pragma warning disable SYSLIB0057   // CreateFromSignedFile has no X509CertificateLoader counterpart
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
        var name = cert.GetNameInfo(X509NameType.SimpleName, false);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath, hFile, pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
