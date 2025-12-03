using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Confluent.Kafka;
using System.Text.Json;
using Chat.Frontend.Services;

namespace Chat.Frontend.Controllers;

[ApiController]
[Route("api/v1/messages")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly IProducer<string, string> _producer;
    private readonly IdempotencyService _idempotency;
    private readonly ILogger<MessagesController> _logger;
    private readonly string _kafkaTopic = "chat.messages";

    public MessagesController(
        IProducer<string, string> producer,
        IdempotencyService idempotency,
        ILogger<MessagesController> logger)
    {
        _producer = producer;
        _idempotency = idempotency;
        _logger = logger;
    }

    /// <summary>
    /// Envia uma nova mensagem
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MessageResponse>> SendMessage(
        [FromBody] SendMessageRequest request)
    {
        // ============================================
        // 1. Validação básica
        // ============================================
        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            _logger.LogWarning("Invalid request: conversation_id is required");
            return BadRequest(new { error = "conversation_id is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            _logger.LogWarning("Invalid request: content is required");
            return BadRequest(new { error = "content is required" });
        }

        if (request.Content.Length > 10000)
        {
            _logger.LogWarning("Invalid request: content too long ({Length} chars)", request.Content.Length);
            return BadRequest(new { error = "content too long (max 10000 characters)" });
        }

        // ============================================
        // 2. Gerar message_id (para idempotência)
        // ============================================
        var messageId = request.MessageId ?? Guid.NewGuid().ToString();
        var userId = User.Identity?.Name ?? "unknown";

        _logger.LogInformation(
            "Processing message: MessageId={MessageId}, UserId={UserId}, ConversationId={ConversationId}",
            messageId,
            userId,
            request.ConversationId);

        // ============================================
        // 3. Verificar duplicata (idempotência)
        // ============================================
        if (await _idempotency.IsDuplicateAsync(messageId))
        {
            _logger.LogInformation(
                "Duplicate message detected: MessageId={MessageId}, returning cached response",
                messageId);

            var cachedResponse = await _idempotency.GetResponseAsync<MessageResponse>(messageId);

            if (cachedResponse != null)
            {
                return Ok(cachedResponse);
            }

            // Se cache expirou, processar normalmente
            _logger.LogWarning("Cached response expired for MessageId={MessageId}, processing again", messageId);
        }

        // ============================================
        // 4. Criar evento para Kafka
        // ============================================
        var evt = new MessageProducedEvent
        {
            MessageId = messageId,
            ConversationId = request.ConversationId,
            SenderId = userId,
            Content = request.Content,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Metadata = new Dictionary<string, string>
            {
                ["client_ip"] = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["user_agent"] = Request.Headers["User-Agent"].ToString() ?? "unknown",
                ["source"] = "api"
            }
        };

        if (request.ReplyToMessageId != null)
        {
            evt.Metadata["reply_to"] = request.ReplyToMessageId;
        }

        var eventJson = JsonSerializer.Serialize(evt, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // ============================================
        // 5. Publicar no Kafka
        // ============================================
        try
        {
            var message = new Message<string, string>
            {
                Key = request.ConversationId,  // Particionamento por conversa (ordem garantida)
                Value = eventJson,
                Headers = new Headers
                {
                    { "message_id", System.Text.Encoding.UTF8.GetBytes(messageId) },
                    { "sender_id", System.Text.Encoding.UTF8.GetBytes(userId) },
                    { "event_type", System.Text.Encoding.UTF8.GetBytes("MessageProduced") }
                }
            };

            var deliveryResult = await _producer.ProduceAsync(_kafkaTopic, message);

            _logger.LogInformation(
                "Message published to Kafka: MessageId={MessageId}, Topic={Topic}, Partition={Partition}, Offset={Offset}",
                messageId,
                deliveryResult.Topic,
                deliveryResult.Partition.Value,
                deliveryResult.Offset.Value);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(
                ex,
                "Failed to publish message to Kafka: MessageId={MessageId}, Error={Error}",
                messageId,
                ex.Error.Reason);

            return StatusCode(503, new
            {
                error = "Failed to publish message",
                message = "Service temporarily unavailable. Please try again."
            });
        }

        // ============================================
        // 6. Criar resposta
        // ============================================
        var response = new MessageResponse
        {
            MessageId = messageId,
            Status = "accepted",
            Timestamp = DateTimeOffset.UtcNow
        };

        // ============================================
        // 7. Salvar resposta para idempotência
        // ============================================
        await _idempotency.SaveResponseAsync(messageId, response);

        _logger.LogInformation(
            "Message accepted: MessageId={MessageId}, Status={Status}",
            messageId,
            response.Status);

        return Accepted(response);
    }

    /// <summary>
    /// Busca mensagens de uma conversa (TODO - implementar na próxima fase)
    /// </summary>
    [HttpGet("{conversationId}")]
    [ProducesResponseType(typeof(ConversationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConversationResponse>> GetConversation(
        string conversationId,
        [FromQuery] int limit = 50,
        [FromQuery] long? beforeSequence = null)
    {
        // TODO: Implementar consulta ao Cassandra
        // Por enquanto, retornar not implemented

        _logger.LogInformation(
            "GetConversation called: ConversationId={ConversationId}, Limit={Limit}, BeforeSequence={BeforeSequence}",
            conversationId,
            limit,
            beforeSequence);

        return StatusCode(501, new
        {
            error = "Not implemented",
            message = "This endpoint will be implemented in the next phase"
        });
    }
}

// ============================================
// DTOs
// ============================================

/// <summary>
/// Request para enviar mensagem
/// </summary>
public record SendMessageRequest
{
    /// <summary>
    /// ID da mensagem (opcional - gerado automaticamente se não fornecido)
    /// Usado para idempotência
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// ID da conversa (ex: "user1_user2" ou UUID)
    /// </summary>
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>
    /// Conteúdo da mensagem
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// ID da mensagem à qual está respondendo (opcional)
    /// </summary>
    public string? ReplyToMessageId { get; init; }
}

/// <summary>
/// Response ao enviar mensagem
/// </summary>
public record MessageResponse
{
    /// <summary>
    /// ID único da mensagem
    /// </summary>
    public string MessageId { get; init; } = string.Empty;

    /// <summary>
    /// Status da mensagem: accepted, queued, delivered, failed
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Timestamp de quando a mensagem foi aceita
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Response ao buscar conversa
/// </summary>
public record ConversationResponse
{
    public string ConversationId { get; init; } = string.Empty;
    public List<MessageItem> Messages { get; init; } = new();
    public long? NextSequence { get; init; }
}

public record MessageItem
{
    public string MessageId { get; init; } = string.Empty;
    public string SenderId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public long SequenceNumber { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Evento publicado no Kafka
/// </summary>
internal record MessageProducedEvent
{
    public string MessageId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string SenderId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public long Timestamp { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}