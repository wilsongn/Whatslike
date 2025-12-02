using Chat.Api.Contracts;
using Chat.Api.Messaging;
using Chat.Persistence.Abstractions;
using Chat.Persistence.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json;

namespace Chat.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1")]
public class MessagesController : ControllerBase
{
    private readonly ILogger<MessagesController> _logger;
    private readonly IMessagePublisher _publisher;
    private readonly IMessageStore _store; // manteremos para GET e (se quiser) modo híbrido
    private readonly IFileMetadataRepository _fileMetadataRepository;

    public MessagesController(IMessageStore store, IMessagePublisher publisher, ILogger<MessagesController> logger, IFileMetadataRepository fileMetadataRepository)
    { _store = store; _publisher = publisher; _logger = logger; _fileMetadataRepository = fileMetadataRepository; }

    // Chat.Api/Controllers/MessagesController.cs
    [HttpPost("v1/messages")]
    public async Task<IActionResult> EnviarMensagem(
    [FromBody] SendMessageRequest request,
    CancellationToken ct)
    {
        // Validação básica por tipo
        switch (request.Tipo)
        {
            case "text":
                {
                    var conteudo = request.Conteudo.Deserialize<ConteudoTexto>();

                    if (conteudo == null || string.IsNullOrWhiteSpace(conteudo.Texto))
                        return BadRequest("Conteúdo de texto vazio.");

                    // aqui você monta a entidade Message de texto
                    // inclui conteudo.Texto no payload, publica em Kafka etc.
                    break;
                }

            case "file":
                {
                    var conteudo = request.Conteudo.Deserialize<ConteudoArquivo>();

                    if (conteudo == null)
                        return BadRequest("Conteúdo de arquivo inválido.");

                    // 1) valida se o arquivo existe na tabela de metadados
                    var meta = await _fileMetadataRepository
                        .GetByIdAsync(conteudo.ArquivoId, ct);

                    if (meta is null)
                        return BadRequest("arquivoId inválido.");

                    if (meta.ConversaId != request.ConversaId)
                        return BadRequest("arquivoId não pertence a essa conversa.");

                    // 2) monta a Message do tipo "file"
                    //    salva no storage de mensagens e publica no Kafka,
                    //    incluindo o ArquivoId (ou o próprio ConteudoArquivo) no payload

                    break;
                }

            default:
                return BadRequest($"Tipo de mensagem inválido: {request.Tipo}");
        }

        // retorno que você já utiliza hoje
        return Accepted();
    }



    // GET /v1/conversations/{conversaId}/messages?bucket=yyyymm&fromSeq=&limit=50
    [HttpGet("conversations/{conversaId:guid}/messages")]
    public async Task<IActionResult> ListMessages(
        [FromRoute] Guid conversaId,
        [FromQuery] int? bucket,
        [FromQuery] long? fromSeq,
        [FromQuery] int? limit)
    {
        var tenantStr = User.FindFirst("tenant_id")?.Value;
        if (!Guid.TryParse(tenantStr, out var organizacaoId))
            return Unauthorized(new { error = new { code = "unauthorized", message = "tenant_id ausente no token" } });

        var _limit = (limit is null or <= 0 or > 200) ? 50 : limit.Value;
        var effectiveBucket = bucket ?? _store.ComputeBucket(DateTimeOffset.UtcNow);

        var items = await _store.ListMessagesAsync(organizacaoId, conversaId, effectiveBucket, fromSeq, _limit);

        var result = items.Select(m => new MessageDto
        {
            MensagemId = m.MensagemId,
            ConversaId = m.ConversaId,
            Sequencia = m.Sequencia,
            Direcao = m.Direcao,
            UsuarioRemetenteId = m.UsuarioRemetenteId,
            Conteudo = JsonSerializer.Deserialize<JsonElement>(m.ConteudoJson),
            Status = m.Status,
            CriadoEm = m.CriadoEm
        }).ToList();

        return Ok(new
        {
            items = result,
            bucket = effectiveBucket,
            count = result.Count
            // futuro: incluir paging_state se adotarmos paginação do driver
        });
    }

    [HttpPost("conversations/{conversaId:guid}/read")]
    public async Task<IActionResult> MarkAsRead([FromRoute] Guid conversaId)
    {
        // 1. Identifica o usuário
        var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value; 
        if (!Guid.TryParse(userIdStr, out var userId)) 
             // Fallback para dev se claim não for GUID
             userId = Guid.NewGuid(); 

        var tenantStr = User.FindFirst("tenant_id")?.Value;
        if (!Guid.TryParse(tenantStr, out var orgId)) orgId = Guid.Empty;

        // 2. Cria evento de leitura
        var evt = new MessageProducedEvent( // Reusando o record existente ou criando um novo genérico
            orgId, conversaId, Guid.Empty, userId, "read-receipt", "internal", "{}", DateTimeOffset.UtcNow
        );
        
        // *Hack*: O MessagePublisher atual espera MessageProducedEvent. 
        // Vamos adaptar ou mandar JSON puro. Para simplificar, mandamos para um tópico "reads".
        // Mas como seu Worker só ouve "messages", vamos usar o mesmo tópico com um payload especial.
        
        // Publica no Kafka (reusando publisher existente)
        await _publisher.PublishAsync(evt);

        return Ok();
    }
}
