// Chat.Api/Repositories/PendingMessageRepository.cs
using Chat.Api.Models;

namespace Chat.Api.Repositories;

public class CassandraPendingMessageRepository : IPendingMessageRepository
{
    private readonly Cassandra.ISession _session;
    private readonly ILogger<CassandraPendingMessageRepository> _logger;

    private readonly Lazy<Cassandra.PreparedStatement> _insertStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _selectStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _deleteStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _deleteAllStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _countStmt;

    public CassandraPendingMessageRepository(Cassandra.ISession session, ILogger<CassandraPendingMessageRepository> logger)
    {
        _session = session;
        _logger = logger;

        _insertStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                INSERT INTO pending_messages (
                    user_id, created_at, message_id, conversation_id, sender_id, sender_name,
                    content, content_type, file_id, file_name, file_extension, file_size,
                    file_organization_id, channel, metadata
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"));

        _selectStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                SELECT * FROM pending_messages 
                WHERE user_id = ? 
                LIMIT ?"));

        _deleteStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                DELETE FROM pending_messages 
                WHERE user_id = ? AND created_at = ? AND message_id = ?"));

        _deleteAllStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("DELETE FROM pending_messages WHERE user_id = ?"));

        _countStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT COUNT(*) FROM pending_messages WHERE user_id = ?"));
    }

    public async Task SaveAsync(PendingMessage message)
    {
        try
        {
            if (message.CreatedAt == default)
            {
                message.CreatedAt = DateTimeOffset.UtcNow;
            }

            if (message.MessageId == Guid.Empty)
            {
                message.MessageId = Guid.NewGuid();
            }

            var bound = _insertStmt.Value.Bind(
                message.UserId,
                message.CreatedAt.UtcDateTime,
                message.MessageId,
                message.ConversationId,
                message.SenderId,
                message.SenderName,
                message.Content,
                message.ContentType,
                message.FileId,
                message.FileName,
                message.FileExtension,
                message.FileSize,
                message.FileOrganizationId,
                message.Channel,
                message.Metadata
            );

            await _session.ExecuteAsync(bound);

            _logger.LogInformation(
                "Pending message saved: UserId={UserId}, MessageId={MessageId}",
                message.UserId, message.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving pending message: {MessageId}", message.MessageId);
            throw;
        }
    }

    public async Task SaveBatchAsync(IEnumerable<PendingMessage> messages)
    {
        try
        {
            var batch = new Cassandra.BatchStatement();
            var now = DateTimeOffset.UtcNow;

            foreach (var message in messages)
            {
                if (message.CreatedAt == default)
                {
                    message.CreatedAt = now;
                }

                if (message.MessageId == Guid.Empty)
                {
                    message.MessageId = Guid.NewGuid();
                }

                batch.Add(_insertStmt.Value.Bind(
                    message.UserId,
                    message.CreatedAt.UtcDateTime,
                    message.MessageId,
                    message.ConversationId,
                    message.SenderId,
                    message.SenderName,
                    message.Content,
                    message.ContentType,
                    message.FileId,
                    message.FileName,
                    message.FileExtension,
                    message.FileSize,
                    message.FileOrganizationId,
                    message.Channel,
                    message.Metadata
                ));
            }

            await _session.ExecuteAsync(batch);

            _logger.LogInformation("Batch of pending messages saved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving batch of pending messages");
            throw;
        }
    }

    public async Task<IEnumerable<PendingMessage>> GetPendingMessagesAsync(Guid userId, int limit = 100)
    {
        try
        {
            var bound = _selectStmt.Value.Bind(userId, limit);
            var result = await _session.ExecuteAsync(bound);

            var messages = new List<PendingMessage>();
            foreach (var row in result)
            {
                messages.Add(MapRowToMessage(row));
            }

            _logger.LogInformation(
                "Retrieved pending messages: UserId={UserId}, Count={Count}",
                userId, messages.Count);

            return messages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting pending messages: {UserId}", userId);
            throw;
        }
    }

    public async Task DeleteAsync(Guid userId, DateTimeOffset createdAt, Guid messageId)
    {
        try
        {
            var bound = _deleteStmt.Value.Bind(userId, createdAt.UtcDateTime, messageId);
            await _session.ExecuteAsync(bound);

            _logger.LogDebug(
                "Pending message deleted: UserId={UserId}, MessageId={MessageId}",
                userId, messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting pending message: {MessageId}", messageId);
            throw;
        }
    }

    public async Task DeleteAllAsync(Guid userId)
    {
        try
        {
            // Cassandra não suporta DELETE sem todas as chaves de clustering
            // Então precisamos buscar e deletar uma a uma
            var messages = await GetPendingMessagesAsync(userId, 1000);

            foreach (var message in messages)
            {
                await DeleteAsync(userId, message.CreatedAt, message.MessageId);
            }

            _logger.LogInformation("All pending messages deleted: UserId={UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting all pending messages: {UserId}", userId);
            throw;
        }
    }

    public async Task DeleteBatchAsync(Guid userId, IEnumerable<Guid> messageIds)
    {
        try
        {
            // Buscar mensagens para obter created_at (necessário para delete)
            var allMessages = await GetPendingMessagesAsync(userId, 1000);
            var messageIdSet = messageIds.ToHashSet();

            var toDelete = allMessages.Where(m => messageIdSet.Contains(m.MessageId)).ToList();

            if (toDelete.Count == 0) return;

            var batch = new Cassandra.BatchStatement();
            foreach (var message in toDelete)
            {
                batch.Add(_deleteStmt.Value.Bind(userId, message.CreatedAt.UtcDateTime, message.MessageId));
            }

            await _session.ExecuteAsync(batch);
            _logger.LogInformation(
                "Batch delete pending messages: UserId={UserId}, Count={Count}",
                userId, toDelete.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error batch deleting pending messages: {UserId}", userId);
            throw;
        }
    }

    public async Task<int> CountAsync(Guid userId)
    {
        try
        {
            var bound = _countStmt.Value.Bind(userId);
            var result = await _session.ExecuteAsync(bound);
            var row = result.FirstOrDefault();

            if (row == null) return 0;

            return (int)row.GetValue<long>(0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting pending messages: {UserId}", userId);
            return 0;
        }
    }

    private static PendingMessage MapRowToMessage(Cassandra.Row row)
    {
        return new PendingMessage
        {
            UserId = row.GetValue<Guid>("user_id"),
            MessageId = row.GetValue<Guid>("message_id"),
            ConversationId = row.GetValue<Guid>("conversation_id"),
            SenderId = row.GetValue<Guid>("sender_id"),
            SenderName = row.GetValue<string>("sender_name"),
            Content = row.GetValue<string>("content"),
            ContentType = row.GetValue<string>("content_type") ?? "text",
            FileId = row.GetValue<string>("file_id"),
            FileName = row.GetValue<string>("file_name"),
            FileExtension = row.GetValue<string>("file_extension"),
            FileSize = row.GetValue<long?>("file_size"),
            FileOrganizationId = row.GetValue<string>("file_organization_id"),
            Channel = row.GetValue<string>("channel"),
            Metadata = row.GetValue<string>("metadata"),
            CreatedAt = row.GetValue<DateTimeOffset>("created_at")
        };
    }
}