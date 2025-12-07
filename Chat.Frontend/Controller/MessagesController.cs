// Chat.Frontend/Controller/MessagesController.cs
// VERSÃO COM BROADCAST VIA REDIS PUB/SUB + DADOS DO ARQUIVO

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Confluent.Kafka;
using System.Security.Claims;
using System.Text.Json;
using Chat.Frontend.Services;
using StackExchange.Redis;

namespace Chat.Frontend.Controller;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly IProducer<string, string> _producer;
    private readonly IdempotencyService _idempotency;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<MessagesController> _logger;
    private readonly string _kafkaTopic = "messages";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MessagesController(
        IProducer<string, string> producer,
        IdempotencyService idempotency,
        IConnectionMultiplexer redis,
        ILogger<MessagesController> logger)
    {
        _producer = producer;
        _idempotency = idempotency;
        _redis = redis;
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
        var timestamp = DateTimeOffset.UtcNow;

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
                    timestamp
                });
            }

            await _idempotency.SetProcessedAsync(messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking idempotency for MessageId={MessageId}", messageId);
        }

        // Normalizar conversationId
        var normalizedConversationId = conversationId;
        if (Guid.TryParse(conversationId, out var convGuid))
        {
            normalizedConversationId = convGuid.ToString();
        }

        // ========== BROADCAST IMEDIATO VIA REDIS PUB/SUB ==========
        try
        {
            var subscriber = _redis.GetSubscriber();
            var redisChannel = $"messages:{normalizedConversationId}";

            // Incluir dados completos do arquivo para download
            var webSocketMessage = new
            {
                type = "message.new",
                messageId = messageId,
                conversationId = normalizedConversationId,
                senderId = userId,
                channel = request.Channel ?? "whatsapp",
                content = new
                {
                    type = string.IsNullOrWhiteSpace(request.FileId) ? "text" : "file",
                    text = request.Content ?? "",
                    fileId = request.FileId ?? ""
                },
                // Dados do arquivo para download (novos campos)
                fileId = request.FileId ?? "",
                fileName = request.FileName ?? "",
                fileExtension = request.FileExtension ?? "",
                fileSize = request.FileSize ?? 0,
                timestamp = timestamp,
                status = "sending"
            };

            var redisJson = JsonSerializer.Serialize(webSocketMessage, _jsonOptions);
            await subscriber.PublishAsync(RedisChannel.Literal(redisChannel), redisJson);

            _logger.LogInformation(
                "Redis broadcast published: Channel={Channel}, MessageId={MessageId}, HasFile={HasFile}",
                redisChannel, messageId, !string.IsNullOrWhiteSpace(request.FileId));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast via Redis: MessageId={MessageId}", messageId);
            // Continuar mesmo se o broadcast falhar
        }

        // ========== PUBLICAR NO KAFKA ==========
        try
        {
            var messageEvent = new
            {
                OrganizacaoId = Guid.TryParse(organizationId, out var orgGuid) ? orgGuid : Guid.Empty,
                ConversaId = Guid.TryParse(conversationId, out var cGuid) ? cGuid : Guid.Empty,
                MensagemId = Guid.TryParse(messageId, out var msgGuid) ? msgGuid : Guid.NewGuid(),
                UsuarioRemetenteId = Guid.TryParse(userId, out var userGuid) ? userGuid : Guid.Empty,
                Direcao = "outbound",
                Canal = request.Channel ?? "whatsapp",
                ConteudoJson = JsonSerializer.Serialize(new
                {
                    type = string.IsNullOrWhiteSpace(request.FileId) ? "text" : "file",
                    content = request.Content ?? "",
                    fileId = request.FileId ?? "",
                    fileName = request.FileName ?? "",
                    fileExtension = request.FileExtension ?? "",
                    fileSize = request.FileSize ?? 0,
                    metadata = new
                    {
                        client_ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        user_agent = Request.Headers["User-Agent"].ToString(),
                        source = "api"
                    }
                }),
                CriadoEm = timestamp
            };

            var json = JsonSerializer.Serialize(messageEvent);
            var kafkaMessage = new Message<string, string>
            {
                Key = conversationId,
                Value = json
            };

            var deliveryResult = await _producer.ProduceAsync(_kafkaTopic, kafkaMessage);

            _logger.LogInformation(
                "Message published to Kafka: MessageId={MessageId}, Topic={Topic}, Partition={Partition}, Offset={Offset}",
                messageId, _kafkaTopic, deliveryResult.Partition.Value, deliveryResult.Offset.Value);

            return Ok(new
            {
                messageId,
                status = "accepted",
                timestamp
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
    public string? FileName { get; set; }
    public string? FileExtension { get; set; }
    public long? FileSize { get; set; }
    public string Channel { get; set; } = "whatsapp";
}