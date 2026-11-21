using System.Globalization;
using System.Text.Json.Nodes;
using static Fusebox.Core.Strings;

namespace Fusebox.Core.Network;

/// <summary>One finished speed test (Mbit/s, ms). Kind null = legacy entry without a kind.</summary>
public sealed record SpeedEntry(DateTime Date, double Down, double Up, double? Ping, SpeedKind? Kind = null, double? DownPeak = null, double? UpPeak = null, double? LoadedPing = null, double? Variation = null);

/// <summary>Last 10 speed results in pref <c>speedHistory</c> (JSON, newest first), the unit text, the data estimate and the history bars.</summary>
public static class SpeedHistory
{
    public const int Max = 10;

    public static List<SpeedEntry> Parse(string json)
    {
        var list = new List<SpeedEntry>();
        try
        {
            if (JsonNode.Parse(json) is not JsonArray a) return list;
            foreach (var n in a.Take(Max))
            {
                if (n is not JsonObject o || Num(o, "down") is not { } down || Num(o, "up") is not { } up) continue;
                if (!DateTime.TryParse(o["date"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)) continue;
                SpeedKind? kind = o["kind"]?.ToString() switch { "quick" => SpeedKind.Quick, "full" => SpeedKind.Full, _ => null };
                list.Add(new(date, down, up, Num(o, "ping"), kind, Num(o, "downPeak"), Num(o, "upPeak"), Num(o, "loadedPing"), Num(o, "variation")));
            }
        }
        catch { list.Clear(); }
        return list;
    }

    static double? Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    public static string ToJson(IEnumerable<SpeedEntry> list)
    {
        var a = new JsonArray();
        foreach (var e in list.Take(Max))
        {
            var o = new JsonObject { ["date"] = e.Date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["down"] = e.Down, ["up"] = e.Up };
            void Put(string k, double? v) { if (v != null) o[k] = v; }
            Put("ping", e.Ping);
            if (e.Kind != null) o["kind"] = e.Kind == SpeedKind.Full ? "full" : "quick";
            Put("downPeak", e.DownPeak); Put("upPeak", e.UpPeak); Put("loadedPing", e.LoadedPing); Put("variation", e.Variation);
            a.Add(o);
        }
        return a.ToJsonString();
    }

    public static List<SpeedEntry> Push(IEnumerable<SpeedEntry> list, SpeedEntry e) => new[] { e }.Concat(list).Take(Max).ToList();

    /// <summary>"412 Mbit/s", or with <paramref name="mb"/> (pref speedMB) "51.5 MB/s".</summary>
    public static string MbitText(double mbit, bool mb) => mb ? Fmt.One(mbit / 8) + " MB/s" : (long)Math.Round(mbit, MidpointRounding.AwayFromZero) + " Mbit/s";

    /// <summary>Estimated data of one test in bytes from the last result (20 % margin); null = unknown (then the cap rules).</summary>
    public static long? Estimate(SpeedKind kind, SpeedEntry? last)
    {
        if (last == null) return null;
        long Dir(double mbit) => Math.Min(SpeedMath.Cap(kind), (long)(mbit * 1.2 * 1e6 / 8 * SpeedMath.Seconds(kind)));
        return Dir(Math.Max(last.Down, last.DownPeak ?? 0)) + Dir(Math.Max(last.Up, last.UpPeak ?? 0));
    }

    /// <summary>"about 90.0 MB" / "at most 1.2 GB".</summary>
    public static string Usage(SpeedKind kind, SpeedEntry? last) =>
        Estimate(kind, last) is { } e ? L("about %@", Fmt.Bytes(e)) : L("at most %@", Fmt.Bytes(SpeedMath.Cap(kind) * 2));

    public static string KindName(SpeedKind? k) => k switch { SpeedKind.Quick => L("%@ test", L("Quick")), SpeedKind.Full => L("%@ test", L("Full")), _ => L("single connection") };

    /// <summary>Bar heights for the last results, oldest first: max(3, 24 × down / max(down)).</summary>
    public static double[] BarHeights(IReadOnlyList<SpeedEntry> newestFirst)
    {
        var top = Math.Max(newestFirst.Select(e => e.Down).DefaultIfEmpty(1).Max(), 1);
        return newestFirst.Reverse().Select(e => Math.Max(3, 24 * e.Down / top)).ToArray();
    }
}
