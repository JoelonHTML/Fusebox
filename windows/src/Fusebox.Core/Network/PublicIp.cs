using System.Text.Json.Nodes;

namespace Fusebox.Core.Network;

public sealed record PublicIp(string Ip, string? Isp, string? Place);

public static class PublicIpParser
{
    /// <summary>Cloudflare's speed.cloudflare.com/meta JSON (clientIp, city, country, colo, asOrganization).</summary>
    public static PublicIp? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject j || j["clientIp"]?.GetValue<string>() is not { Length: < 64 } ip) return null;
            var place = string.Join(", ", new[] { j["city"]?.GetValue<string>(), j["country"]?.GetValue<string>() }.Where(x => !string.IsNullOrEmpty(x)));
            var colo = j["colo"]?.GetValue<string>() is { } c ? $" (Cloudflare {c})" : "";
            return new(ip, j["asOrganization"]?.GetValue<string>(), place.Length == 0 ? null : place + colo);
        }
        catch { return null; }
    }
}
