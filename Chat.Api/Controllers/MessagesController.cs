using System.Security.Claims;
using System.Text.Json;
using Chat.Api.Contracts;
using Chat.Api.Messaging;
using Chat.Persistence.Abstractions;
using Chat.Persistence.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chat.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1")]
public class MessagesController : ControllerBase
{
    private readonly ILogger<MessagesController> _logger;
    private readonly IMessagePublisher _publisher;
    private readonly IMessageStore _store; // manteremos para GET e (se quiser) modo híbrido

    public MessagesController(IMessageStore store, IMessagePublisher publisher, ILogger<MessagesController> logger)
    { _store = store; _publisher = publisher; _logger = logger; }

    [HttpPost("messages")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
    {
        var tenantStr = User.FindFirst("tenant_id")?.Value;
        if (!Guid.TryParse(tenantStr, out var organizacaoId))
            return Unauthorized(new { error = new { code = "unauthorized", message = "tenant_id ausente no token" } });

        if (req.ConversaId == Guid.Empty || req.UsuarioRemetenteId == Guid.Empty)
            return BadRequest(new { error = new { code = "bad_request", message = "ConversaId e UsuarioRemetenteId são obrigatórios" } });

        var criado = req.CriadoEm ?? DateTimeOffset.UtcNow;
        var mensagemId = Guid.NewGuid();

        var evt = new MessageProducedEvent(
            OrganizacaoId: organizacaoId,
            ConversaId: req.ConversaId,
            MensagemId: mensagemId,
            UsuarioRemetenteId: req.UsuarioRemetenteId,
            Direcao: string.IsNullOrWhiteSpace(req.Direcao) ? "outbound" : req.Direcao!,
            ConteudoJson: System.Text.Json.JsonSerializer.Serialize(req.Conteudo),
            CriadoEm: criado
        );

        await _publisher.PublishAsync(evt);

        return Accepted(new { mensagemId, conversaId = req.ConversaId });
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
}
