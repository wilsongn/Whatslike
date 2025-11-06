namespace Chat.Api.Messaging;

public sealed record MessageProducedEvent(
    Guid OrganizacaoId,
    Guid ConversaId,
    Guid MensagemId,
    Guid UsuarioRemetenteId,
    string Direcao,
    string ConteudoJson,
    DateTimeOffset CriadoEm
);
