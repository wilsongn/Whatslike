namespace Chat.Shared.Protocol
{
    /// <summary>
    /// Envelopa qualquer mensagem do protocolo.
    /// O Payload é JSON (camelCase) para facilitar a (de)serialização.
    /// </summary>
    public record Envelope(MessageType Type, string? From, string? To, string Payload);
}
