namespace Chat.Persistence.Models;

public sealed class MessageRecord
{
    public Guid OrganizacaoId { get; set; }
    public Guid ConversaId { get; set; }
    public int Bucket { get; set; }         
    public long Sequencia { get; set; }     
    public Guid MensagemId { get; set; }
    public string Direcao { get; set; } = "outbound";
    public Guid UsuarioRemetenteId { get; set; }
    public string ConteudoJson { get; set; } = "{}";
    public string Status { get; set; } = "sent";
    public DateTimeOffset CriadoEm { get; set; } = DateTimeOffset.UtcNow;
}
