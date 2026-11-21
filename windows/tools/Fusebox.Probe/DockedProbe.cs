using System.Text.Json.Nodes;
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
            ["onAc"] = s.Power.OnAc, ["hasBattery"] = s.Power.HasBattery,
            ["usbDevices"] = s.Usb.Count,
            ["usb"] = new JsonArray(s.Usb.Select(u => (JsonNode)JsonValue.Create($"{new string(' ', u.Depth * 2)}{u.Name} {u.Speed} (max {u.Max}){(u.Slow ? " SLOW" : "")}{(u.TypeC == true ? " USB-C" : "")}")!).ToArray()),
            ["displays"] = new JsonArray(s.Displays.Select(d => (JsonNode)JsonValue.Create($"{d.Name} {d.Width}x{d.Height} {d.Hz} Hz {d.Connection} hdr={d.Hdr}")!).ToArray()),
            ["bluetooth"] = s.Bluetooth == null ? null : new JsonArray(s.Bluetooth.Select(b => (JsonNode)JsonValue.Create($"{b.Name} connected={b.Connected} battery={b.Battery}")!).ToArray()),
            ["audio"] = new JsonArray(s.Audio.Select(a => (JsonNode)JsonValue.Create($"{(a.IsOutput ? "out" : "in")} {a.Name} [{a.Kind}] default={a.IsDefault} plugged={a.Plugged}")!).ToArray()),
        };
        foreach (var e in errors) failures.Add("docked " + e);
    }
}
