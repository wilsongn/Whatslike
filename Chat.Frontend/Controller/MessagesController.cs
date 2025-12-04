// Chat.Frontend/Controllers/MessagesController.cs
// ARQUIVO COMPLETO - Substituir todo o conteúdo

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Confluent.Kafka;
using System.Security.Claims;
using System.Text.Json;
using Chat.Frontend.Services;

namespace Chat.Frontend.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly IProducer<string, string> _producer;
    private readonly IdempotencyService _idempotency;
    private readonly ILogger<MessagesController> _logger;
    private readonly string _kafkaTopic = "messages";

    public MessagesController(
        IProducer<string, string> producer,
        IdempotencyService idempotency,
        ILogger<MessagesController> logger)
    {
        _producer = producer;
        _idempotency = idempotency;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content) && string.IsNullOrWhiteSpace(request.FileId))
        {
            return BadRequest(new { error = "Content or FileId is required" });
        }

        // Extrair dados do JWT
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
        var organizationId = User.FindFirst("tenant_id")?.Value ?? Guid.Empty.ToString();

        var messageId = string.IsNullOrWhiteSpace(request.MessageId)
            ? Guid.NewGuid().ToString()
            : request.MessageId;

        var conversationId = request.ConversationId;

        _logger.LogInformation(
            "Processing message: MessageId={MessageId}, UserId={UserId}, ConversationId={ConversationId}",
            messageId, userId, conversationId);

        // Verificar duplicação (idempotência)
        try
        {
            var isDuplicate = await _idempotency.IsDuplicateAsync(messageId);
            if (isDuplicate)
            {
                _logger.LogWarning("Duplicate message detected: MessageId={MessageId}", messageId);
                return Ok(new
                {
                    messageId,
                    status = "duplicate",
                    timestamp = DateTimeOffset.UtcNow
                });
            }

            // Marcar como processado
            await _idempotency.SetProcessedAsync(messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking idempotency for MessageId={MessageId}", messageId);
            // Continuar mesmo com erro de idempotência
        }

        // Publicar no Kafka
        try
        {
            // Criar evento no formato esperado pelo RouterWorker
            var messageEvent = new
            {
                OrganizacaoId = Guid.TryParse(organizationId, out var orgGuid) ? orgGuid : Guid.Empty,
                ConversaId = Guid.TryParse(conversationId, out var convGuid) ? convGuid : Guid.Empty,
                MensagemId = Guid.TryParse(messageId, out var msgGuid) ? msgGuid : Guid.NewGuid(),
                UsuarioRemetenteId = Guid.TryParse(userId, out var userGuid) ? userGuid : Guid.Empty,
                Direcao = "outbound",
                ConteudoJson = JsonSerializer.Serialize(new
                {
                    type = string.IsNullOrWhiteSpace(request.FileId) ? "text" : "file",
                    content = request.Content ?? "",
                    fileId = request.FileId ?? "",
                    metadata = new
                    {
                        client_ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        user_agent = Request.Headers["User-Agent"].ToString(),
                        source = "api"
                    }
                }),
                CriadoEm = DateTimeOffset.UtcNow
            };

            var messageJson = JsonSerializer.Serialize(messageEvent);
            var message = new Message<string, string>
            {
                Key = conversationId,
                Value = messageJson
            };

            var deliveryResult = await _producer.ProduceAsync(_kafkaTopic, message);

            _logger.LogInformation(
                "Message published to Kafka: MessageId={MessageId}, Topic={Topic}, Partition={Partition}, Offset={Offset}",
                messageId,
                deliveryResult.Topic,
                deliveryResult.Partition.Value,
                deliveryResult.Offset.Value);

            _logger.LogInformation(
                "Message accepted: MessageId={MessageId}, Status={Status}",
                messageId, "accepted");

            return Ok(new
            {
                messageId,
                status = "accepted",
                timestamp = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish message to Kafka: MessageId={MessageId}", messageId);
            return StatusCode(500, new
            {
                error = "Failed to send message",
                messageId
            });
        }
    }
}

public class SendMessageRequest
{
    public string? MessageId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string? Content { get; set; }
    public string? FileId { get; set; }
}