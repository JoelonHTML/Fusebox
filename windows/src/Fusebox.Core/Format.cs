using System.Globalization;
using static Fusebox.Core.Strings;

namespace Fusebox.Core;

/// <summary>
/// Number formatting exactly like the Mac (FEATURES.md §3.4, Meten.swift/Metrics.swift): en_US digits with a dot in every
/// UI language, decimal byte units (1 KB = 1000 B). Fusebox prints KB/MB/GB itself in every language (glossary).
/// </summary>
public static class Fmt
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>"23%"; integer rounding, so never "−0%". NaN = "–".</summary>
    public static string Pct(double v) => double.IsFinite(v) ? $"{Round(v)}%" : "–";
    public static string Percent(double v) => Pct(v);

    /// <summary>Exactly one decimal, en_US ("12.4", "1,234.5").</summary>
    public static string One(double v) => v.ToString("#,0.0", En);

    /// <summary>"640 KB" (integer, truncated) below 1 MB, then "12.3 MB", "1.2 GB".</summary>
    public static string Bytes(double b) => b < 1e6 ? $"{(long)(b / 1e3)} KB" : b < 1e9 ? $"{One(b / 1e6)} MB" : $"{One(b / 1e9)} GB";

    public static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";

    /// <summary>Rate in bytes, or with <paramref name="bits"/> (pref netBits) "640 kbit/s" / "5.1 Mbit/s".</summary>
    public static string Short(double bps, bool bits = false)
    {
        if (!bits) return Rate(bps);
        double b = bps * 8;
        return b < 1_000_000 ? $"{(long)(b / 1000)} kbit/s" : $"{One(b / 1_000_000)} Mbit/s";
    }

    /// <summary>
    /// Two rates with one shared unit chosen by the larger (NET ↓/↑, I/O R/W): ("8", "120", "KB/s"), ("1.2", "34", "MB/s").
    /// Integers, one decimal for MB/GB (Mbit/Gbit) values below 10; negatives count as 0.
    /// </summary>
    public static (string A, string B, string Unit) RatePair(double a, double b, bool bits = false)
    {
        double k = bits ? 8 : 1, m = Math.Max(Math.Max(a, b), 0) * k;
        if (double.IsNaN(m)) m = 0;
        var (div, unit) = m < 1e6 ? (1e3, bits ? "kbit/s" : "KB/s") : m < 1e9 ? (1e6, bits ? "Mbit/s" : "MB/s") : (1e9, bits ? "Gbit/s" : "GB/s");
        string F(double v) { double x = (double.IsFinite(v) ? Math.Max(v, 0) : 0) * k / div; return div > 1e3 && x < 10 ? One(x) : Round(x).ToString(En); }
        return (F(a), F(b), unit);
    }

    /// <summary>"42°", "" when unknown.</summary>
    public static string Deg(double t) => double.IsFinite(t) ? $"{Round(t)}°" : "";

    /// <summary>"48m", "2h 35m", "1d 3h".</summary>
    public static string Duration(int minutes) =>
        minutes >= 1440 ? $"{minutes / 1440}d {minutes % 1440 / 60}h" : minutes >= 60 ? $"{minutes / 60}h {minutes % 60}m" : $"{minutes}m";

    /// <summary>Battery side text: "2:35", or "14h" from 600 minutes (so it doesn't read as a clock time). "" when unknown.</summary>
    public static string BatteryTime(double minutes)
    {
        if (!double.IsFinite(minutes) || minutes < 0) return "";
        long m = Round(minutes);
        return m >= 600 ? $"{m / 60}h" : $"{m / 60}:{m % 60:00}";
    }

    /// <summary>2 = danger, 1 = warn, 0 = fine.</summary>
    public static int Level(double v, double warn, double danger) => v >= danger ? 2 : v >= warn ? 1 : 0;

    /// <summary>Fan speed "1,850".</summary>
    public static string Rpm(double rpm) => Round(rpm).ToString("#,0", En);
}

/// <summary>Memory pressure like the Mac's levels: 1 normal, 2 high, 4 critical. Windows: from available physical memory.</summary>
public static class MemoryPressure
{
    public static int FromRamPercent(double usedPercent)
    {
        if (!double.IsFinite(usedPercent)) return 1;
        double free = 100 - usedPercent;
        return free < 5 ? 4 : free < 10 ? 2 : 1;
    }
}

/// <summary>The overall status sentence (§8.5, §11.1) for the tray menu, the advice line and headers. "PC" instead of "Mac".</summary>
public static class StatusSentence
{
    /// <summary>Text (translated) and level: 0 green, 1 orange, 2 red. First match wins.</summary>
    /// <param name="cpu30">Average CPU % over the last 30 s.</param>
    /// <param name="temp">Hottest CPU °C, NaN when unknown (Windows: usually unknown).</param>
    public static (string Text, int Level) Of(int pressure, double temp, double cpu30, double warnCpu = 85, double warnTemp = 95)
    {
        if (pressure >= 4) return (L("Memory is full — close a few apps"), 2);
        if (temp >= warnTemp + 10) return (L("Your PC is very hot — it's slowing itself down"), 2);
        if (cpu30 >= warnCpu) return (L("Your PC is busy"), 1);
        if (pressure == 2) return (L("Memory is filling up"), 1);
        if (temp >= warnTemp) return (L("Your PC is getting warm"), 1);
        return (L("All calm"), 0);
    }
}
