namespace Fusebox.Core.Update;

/// <summary>Pure checks around installing an update: CPU architecture of the new exe, rollback decision, install folder.</summary>
public static class UpdateSafety
{
    public const ushort MachineX64 = 0x8664, MachineArm64 = 0xAA64;

    /// <summary>Machine field of a PE header (first bytes of an .exe), or null when it is not a PE file.</summary>
    public static ushort? PeMachine(ReadOnlySpan<byte> h)
    {
        if (h.Length < 0x40 || h[0] != 'M' || h[1] != 'Z') return null;
        int pe = BitConverter.ToInt32(h.Slice(0x3C, 4));
        if (pe < 0 || pe > h.Length - 6) return null;
        if (h[pe] != 'P' || h[pe + 1] != 'E' || h[pe + 2] != 0 || h[pe + 3] != 0) return null;
        return BitConverter.ToUInt16(h.Slice(pe + 4, 2));
    }

    public static bool MachineMatches(ushort? machine, bool arm64) => machine == (arm64 ? MachineArm64 : MachineX64);

    /// <summary>The new build never confirmed it starts (marker still there after the grace period) and there is something to go back to.</summary>
    public static bool ShouldRollBack(TimeSpan? markerAge, bool oldFilesExist) =>
        markerAge is { } a && a > TimeSpan.FromMinutes(2) && oldFilesExist;

    /// <summary>Automatic installs only for the copy in %LOCALAPPDATA%\Programs\Fusebox.</summary>
    public static bool IsInstallDir(string appDir, string localAppData) =>
        string.Equals(Norm(appDir), Norm(localAppData) + @"\Programs\Fusebox", StringComparison.OrdinalIgnoreCase);

    static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');
}
