using System.Text.Json;

namespace Chat.Api.Contracts;

public sealed class SendMessageRequest
{
    public Guid ConversaId { get; set; }
    public Guid UsuarioRemetenteId { get; set; }
    public string Tipo { get; set; } = "text";
    public string? Direcao { get; set; } = "outbound";
    public string Canal { get; set; } = "";
    public JsonElement Conteudo { get; set; }         
    public DateTimeOffset? CriadoEm { get; set; }     
}
