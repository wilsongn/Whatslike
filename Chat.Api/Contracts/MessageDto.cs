using System.Text.Json;

namespace Chat.Api.Contracts;

public sealed class MessageDto
{
    public Guid MensagemId { get; set; }
    public Guid ConversaId { get; set; }
    public long Sequencia { get; set; }
    public string Direcao { get; set; } = "outbound";
    public Guid UsuarioRemetenteId { get; set; }
    public JsonElement Conteudo { get; set; }
    public string Status { get; set; } = "sent";
    public DateTimeOffset CriadoEm { get; set; }
}
