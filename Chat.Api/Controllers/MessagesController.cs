using System.Security.Claims;
using System.Text.Json;
using Chat.Api.Contracts;
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
    private readonly IMessageStore _store;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(IMessageStore store, ILogger<MessagesController> logger)
    {
        _store = store;
        _logger = logger;
    }

    // POST /v1/messages
    [HttpPost("messages")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest req)
    {
        // tenant do JWT
        var tenantStr = User.FindFirst("tenant_id")?.Value;
        if (!Guid.TryParse(tenantStr, out var organizacaoId))
            return Unauthorized(new { error = new { code = "unauthorized", message = "tenant_id ausente no token" } });

        if (req.ConversaId == Guid.Empty || req.UsuarioRemetenteId == Guid.Empty)
            return BadRequest(new { error = new { code = "bad_request", message = "ConversaId e UsuarioRemetenteId são obrigatórios" } });

        var criado = req.CriadoEm ?? DateTimeOffset.UtcNow;

        var record = new MessageRecord
        {
            OrganizacaoId = organizacaoId,
            ConversaId = req.ConversaId,
            MensagemId = Guid.NewGuid(),
            UsuarioRemetenteId = req.UsuarioRemetenteId,
            Direcao = string.IsNullOrWhiteSpace(req.Direcao) ? "outbound" : req.Direcao!,
            ConteudoJson = JsonSerializer.Serialize(req.Conteudo),
            Status = "sent",
            CriadoEm = criado
        };

        var seq = await _store.InsertMessageAsync(record);

        var dto = new MessageDto
        {
            MensagemId = record.MensagemId,
            ConversaId = record.ConversaId,
            Sequencia = seq,
            Direcao = record.Direcao,
            UsuarioRemetenteId = record.UsuarioRemetenteId,
            Conteudo = JsonSerializer.Deserialize<JsonElement>(record.ConteudoJson),
            Status = record.Status,
            CriadoEm = record.CriadoEm
        };

        // 201 com Location para o recurso lógico (timeline da conversa)
        return Created($"/v1/conversations/{record.ConversaId}/messages?fromSeq={seq}", dto);
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
