using System.Text.Json;

namespace Chat.Api.Contracts;

public sealed class SendMessageRequest
{
    public Guid ConversaId { get; set; }
    public Guid UsuarioRemetenteId { get; set; }
    public string? Direcao { get; set; } = "outbound";
    public JsonElement Conteudo { get; set; }         
    public DateTimeOffset? CriadoEm { get; set; }     
}
