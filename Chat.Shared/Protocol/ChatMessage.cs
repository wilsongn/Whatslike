using System.Text.Json.Serialization;

namespace Chat.Shared.Protocol
{
    /// <summary>
    /// Payload de mensagem de texto usado em PrivateMsg e GroupMsg.
    /// </summary>
    public sealed record ChatMessage(
        [property: JsonPropertyName("text")] string Text
    );
}
