using System.Text.Json.Nodes;
using Fusebox.Core.Docked;
using Fusebox.Platform.Docked;

public static class DockedProbe
{
    /// <summary>Empty lists are fine on a CI VM; only a reader that throws is a failure.</summary>
    public static void Run(JsonObject o, List<string> failures)
    {
        var errors = new List<string>();
        var s = DockedReader.Read(errors);
        o["docked"] = new JsonObject
        {
            ["onAc"] = s.Power.OnAc, ["hasBattery"] = s.Power.HasBattery, ["watts"] = s.Power.Watts, ["minutes"] = s.Power.Minutes,
            ["uplink"] = s.Uplink == null ? null : $"{s.Uplink.Kind} metered={s.Uplink.Metered}",
            ["usbDevices"] = s.Usb.Count,
            ["usb"] = new JsonArray(s.Usb.Select(u => (JsonNode)JsonValue.Create($"{new string(' ', u.Depth * 2)}{u.Name} {u.Speed} (max {u.Max}){(u.Slow ? " SLOW" : "")}{(u.TypeC == true ? " USB-C" : "")} path={u.Path} vid={u.Vid:X4} ma={u.Ma} port={u.Connectable}")!).ToArray()),
            ["displays"] = new JsonArray(s.Displays.Select(d => (JsonNode)JsonValue.Create($"{d.Name} {d.Width}x{d.Height} {d.Hz} Hz {d.Connection} hdr={d.Hdr} native={d.NativeW}x{d.NativeH}@{d.MaxHz} scale={d.Scale} bits={d.Bits}")!).ToArray()),
            ["bluetooth"] = s.Bluetooth == null ? null : new JsonArray(s.Bluetooth.Select(b => (JsonNode)JsonValue.Create($"{b.Name} connected={b.Connected} battery={b.Battery} kind={b.Kind}")!).ToArray()),
            ["audio"] = new JsonArray(s.Audio.Select(a => (JsonNode)JsonValue.Create($"{(a.IsOutput ? "out" : "in")} {a.Name} [{a.Kind}] default={a.IsDefault} plugged={a.Plugged}")!).ToArray()),
        };
        try
        {
            var v = DockedBuilder.Build(s, new DockedOptions(Internals: true));
            o["docked"]!["view"] = $"{v.Ports.Count} port groups, {v.Wireless.Count} wireless, other={v.Other?.Rows.Count ?? 0}, header='{v.Header.Big}'";
        }
        catch (Exception e) { failures.Add("docked view " + e.Message); }
        foreach (var e in errors) failures.Add("docked " + e);
    }
}
