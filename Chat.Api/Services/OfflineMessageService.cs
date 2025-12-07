// Chat.Api/Services/OfflineMessageService.cs
using Chat.Api.Models;
using Chat.Api.Repositories;
using StackExchange.Redis;
using System.Text.Json;

namespace Chat.Api.Services;

public interface IOfflineMessageService
{
    /// <summary>
    /// Processa o envio de uma mensagem, salvando como pendente se destinatário estiver offline
    /// </summary>
    Task<bool> ProcessMessageDeliveryAsync(
        Guid conversationId,
        Guid senderId,
        string senderName,
        Guid recipientId,
        Guid messageId,
        string? content,
        string contentType = "text",
        string? fileId = null,
        string? fileName = null,
        string? fileExtension = null,
        long? fileSize = null,
        string? fileOrganizationId = null,
        string? channel = null);
    
    /// <summary>
    /// Entrega mensagens pendentes para um usuário que acabou de ficar online
    /// </summary>
    Task DeliverPendingMessagesAsync(Guid userId);
    
    /// <summary>
    /// Confirma recebimento de mensagens pendentes
    /// </summary>
    Task AcknowledgeMessagesAsync(Guid userId, IEnumerable<Guid> messageIds);
    
    /// <summary>
    /// Obtém mensagens pendentes de um usuário
    /// </summary>
    Task<IEnumerable<PendingMessage>> GetPendingMessagesAsync(Guid userId);
    
    /// <summary>
    /// Conta mensagens pendentes de um usuário
    /// </summary>
    Task<int> GetPendingCountAsync(Guid userId);
}

public class OfflineMessageService : IOfflineMessageService
{
    private readonly IPendingMessageRepository _pendingMessageRepository;
    private readonly IPresenceRepository _presenceRepository;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<OfflineMessageService> _logger;
    
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public OfflineMessageService(
        IPendingMessageRepository pendingMessageRepository,
        IPresenceRepository presenceRepository,
        IConnectionMultiplexer redis,
        ILogger<OfflineMessageService> logger)
    {
        _pendingMessageRepository = pendingMessageRepository;
        _presenceRepository = presenceRepository;
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> ProcessMessageDeliveryAsync(
        Guid conversationId,
        Guid senderId,
        string senderName,
        Guid recipientId,
        Guid messageId,
        string? content,
        string contentType = "text",
        string? fileId = null,
        string? fileName = null,
        string? fileExtension = null,
        long? fileSize = null,
        string? fileOrganizationId = null,
        string? channel = null)
    {
        try
        {
            // Verificar se destinatário está online
            var isOnline = await _presenceRepository.IsOnlineAsync(recipientId);
            
            if (isOnline)
            {
                _logger.LogDebug(
                    "Recipient is online, message will be delivered via WebSocket: RecipientId={RecipientId}",
                    recipientId);
                return true; // Online - será entregue via WebSocket/Redis Pub/Sub
            }
            
            // Destinatário offline - salvar mensagem pendente
            var pendingMessage = new PendingMessage
            {
                UserId = recipientId,
                MessageId = messageId,
                ConversationId = conversationId,
                SenderId = senderId,
                SenderName = senderName,
                Content = content,
                ContentType = contentType,
                FileId = fileId,
                FileName = fileName,
                FileExtension = fileExtension,
                FileSize = fileSize,
                FileOrganizationId = fileOrganizationId,
                Channel = channel,
                CreatedAt = DateTimeOffset.UtcNow
            };
            
            await _pendingMessageRepository.SaveAsync(pendingMessage);
            
            _logger.LogInformation(
                "Message saved as pending (recipient offline): RecipientId={RecipientId}, MessageId={MessageId}",
                recipientId, messageId);
            
            return false; // Offline - salvo como pendente
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message delivery: MessageId={MessageId}", messageId);
            throw;
        }
    }

    public async Task DeliverPendingMessagesAsync(Guid userId)
    {
        try
        {
            var pendingMessages = await _pendingMessageRepository.GetPendingMessagesAsync(userId);
            var messageList = pendingMessages.ToList();
            
            if (messageList.Count == 0)
            {
                _logger.LogDebug("No pending messages for user: {UserId}", userId);
                return;
            }
            
            _logger.LogInformation(
                "Delivering pending messages: UserId={UserId}, Count={Count}",
                userId, messageList.Count);
            
            var subscriber = _redis.GetSubscriber();
            var deliveredIds = new List<Guid>();
            
            foreach (var message in messageList)
            {
                try
                {
                    // Publicar via Redis para o WebSocket entregar
                    var notification = new
                    {
                        type = "pending.message",
                        messageId = message.MessageId,
                        conversationId = message.ConversationId,
                        senderId = message.SenderId,
                        senderName = message.SenderName,
                        content = message.Content,
                        contentType = message.ContentType,
                        fileId = message.FileId,
                        fileName = message.FileName,
                        fileExtension = message.FileExtension,
                        fileSize = message.FileSize,
                        fileOrganizationId = message.FileOrganizationId,
                        channel = message.Channel,
                        createdAt = message.CreatedAt,
                        isPending = true
                    };
                    
                    var json = JsonSerializer.Serialize(notification, _jsonOptions);
                    
                    // Publicar no canal do usuário
                    await subscriber.PublishAsync(
                        RedisChannel.Literal($"user:{userId}"),
                        json
                    );
                    
                    deliveredIds.Add(message.MessageId);
                    
                    _logger.LogDebug(
                        "Pending message delivered via Redis: UserId={UserId}, MessageId={MessageId}",
                        userId, message.MessageId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error delivering pending message: {MessageId}", message.MessageId);
                }
            }
            
            // Remover mensagens entregues
            if (deliveredIds.Count > 0)
            {
                await _pendingMessageRepository.DeleteBatchAsync(userId, deliveredIds);
                
                _logger.LogInformation(
                    "Pending messages delivered and removed: UserId={UserId}, Count={Count}",
                    userId, deliveredIds.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error delivering pending messages: {UserId}", userId);
        }
    }

    public async Task AcknowledgeMessagesAsync(Guid userId, IEnumerable<Guid> messageIds)
    {
        try
        {
            await _pendingMessageRepository.DeleteBatchAsync(userId, messageIds);
            
            _logger.LogInformation(
                "Messages acknowledged: UserId={UserId}, Count={Count}",
                userId, messageIds.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acknowledging messages: {UserId}", userId);
            throw;
        }
    }

    public async Task<IEnumerable<PendingMessage>> GetPendingMessagesAsync(Guid userId)
    {
        return await _pendingMessageRepository.GetPendingMessagesAsync(userId);
    }

    public async Task<int> GetPendingCountAsync(Guid userId)
    {
        return await _pendingMessageRepository.CountAsync(userId);
    }
}
