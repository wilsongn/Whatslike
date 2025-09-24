using System.Text.Json;

namespace Chat.Shared.Protocol;

public sealed record Routed(string OriginNode, string TargetNode, string EnvelopeJson, string[]? Targets = null)
{
    public static string Serialize(Routed r) => JsonSerializer.Serialize(r);
    public static Routed? Deserialize(string json) => JsonSerializer.Deserialize<Routed>(json);
}
