using static Fusebox.Core.Strings;

namespace Fusebox.Core;

public static class Fmt
{
    /// <summary>Decimal units like the Mac (1 KB = 1000 B). Fusebox prints GB itself in every language (glossary).</summary>
    public static string Bytes(double b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (Math.Abs(b) >= 1000 && i < u.Length - 1) { b /= 1000; i++; }
        return (i == 0 ? b.ToString("0", Culture) : b.ToString(b < 10 ? "0.0" : "0", Culture)) + " " + u[i];
    }

    public static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";
    public static string Percent(double p) => p.ToString("0", Culture) + "%";
}
