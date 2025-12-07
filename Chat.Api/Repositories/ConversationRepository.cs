// Chat.Api/Repositories/ConversationRepository.cs
using Chat.Api.Models;

namespace Chat.Api.Repositories;

public class CassandraConversationRepository : IConversationRepository
{
    private readonly Cassandra.ISession _session;
    private readonly ILogger<CassandraConversationRepository> _logger;
    
    // Prepared statements
    private readonly Lazy<Cassandra.PreparedStatement> _insertConversationStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _selectConversationStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _insertMemberStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _deleteMemberStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _selectMembersStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _selectMemberStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _insertUserConversationStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _deleteUserConversationStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _selectUserConversationsStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _insertPrivateConversationStmt;
    private readonly Lazy<Cassandra.PreparedStatement> _selectPrivateConversationStmt;

    public CassandraConversationRepository(Cassandra.ISession session, ILogger<CassandraConversationRepository> logger)
    {
        _session = session;
        _logger = logger;
        
        _insertConversationStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                INSERT INTO conversations (conversation_id, organization_id, type, name, description, avatar_url, created_by, created_at, updated_at, metadata)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"));
        
        _selectConversationStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT * FROM conversations WHERE conversation_id = ?"));
        
        _insertMemberStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                INSERT INTO conversation_members (conversation_id, user_id, role, joined_at, added_by, nickname, notifications_enabled)
                VALUES (?, ?, ?, ?, ?, ?, ?)"));
        
        _deleteMemberStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("DELETE FROM conversation_members WHERE conversation_id = ? AND user_id = ?"));
        
        _selectMembersStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT * FROM conversation_members WHERE conversation_id = ?"));
        
        _selectMemberStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT * FROM conversation_members WHERE conversation_id = ? AND user_id = ?"));
        
        _insertUserConversationStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                INSERT INTO user_conversations (user_id, last_message_at, conversation_id, type, name, avatar_url, last_message_preview, last_message_sender, unread_count, is_muted, is_pinned)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"));
        
        _deleteUserConversationStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("DELETE FROM user_conversations WHERE user_id = ? AND last_message_at = ? AND conversation_id = ?"));
        
        _selectUserConversationsStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT * FROM user_conversations WHERE user_id = ? LIMIT ?"));
        
        _insertPrivateConversationStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare(@"
                INSERT INTO private_conversations (organization_id, user_pair, conversation_id, created_at)
                VALUES (?, ?, ?, ?)"));
        
        _selectPrivateConversationStmt = new Lazy<Cassandra.PreparedStatement>(() =>
            _session.Prepare("SELECT * FROM private_conversations WHERE organization_id = ? AND user_pair = ?"));
    }

    public async Task UpdateLastMessageAsync(
        Guid userId,
        Guid conversationId,
        string preview,
        string senderName,
        DateTimeOffset timestamp)
    {
        try
        {
            // Buscar conversa atual do usuário
            var conversations = await GetUserConversationsAsync(userId, 100);
            var conv = conversations.FirstOrDefault(c => c.ConversationId == conversationId);

            if (conv != null)
            {
                // Deletar entrada antiga
                var deleteStmt = await _session.PrepareAsync(
    "DELETE FROM user_conversations WHERE user_id = ? AND last_message_at = ? AND conversation_id = ?");
                var lastMsgAt = conv.LastMessageAt == default ? DateTime.UtcNow : conv.LastMessageAt.UtcDateTime;
                await _session.ExecuteAsync(deleteStmt.Bind(userId, lastMsgAt, conversationId));

                // Inserir com novo timestamp
                var insertStmt = await _session.PrepareAsync(@"
                    INSERT INTO user_conversations (
                        user_id, last_message_at, conversation_id, type, name, avatar_url,
                        last_message_preview, last_message_sender, unread_count, is_muted, is_pinned
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)");

                await _session.ExecuteAsync(insertStmt.Bind(
                    userId,
                    timestamp.UtcDateTime,
                    conversationId,
                    conv.Type,
                    conv.Name,
                    conv.AvatarUrl,
                    preview,
                    senderName,
                    conv.UnreadCount,
                    conv.IsMuted,
                    conv.IsPinned
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating last message for user {UserId}, conversation {ConversationId}", userId, conversationId);
        }
    }

    public async Task<Conversation> CreateAsync(Conversation conversation)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            conversation.CreatedAt = now;
            conversation.UpdatedAt = now;
            
            if (conversation.ConversationId == Guid.Empty)
            {
                conversation.ConversationId = Guid.NewGuid();
            }
            
            var bound = _insertConversationStmt.Value.Bind(
                conversation.ConversationId,
                conversation.OrganizationId,
                conversation.Type,
                conversation.Name,
                conversation.Description,
                conversation.AvatarUrl,
                conversation.CreatedBy,
                conversation.CreatedAt.UtcDateTime,
                conversation.UpdatedAt.UtcDateTime,
                conversation.Metadata
            );
            
            await _session.ExecuteAsync(bound);
            
            _logger.LogInformation(
                "Conversation created: ConversationId={ConversationId}, Type={Type}",
                conversation.ConversationId, conversation.Type);
            
            return conversation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating conversation");
            throw;
        }
    }

    public async Task<Conversation?> GetByIdAsync(Guid conversationId)
    {
        try
        {
            var bound = _selectConversationStmt.Value.Bind(conversationId);
            var result = await _session.ExecuteAsync(bound);
            var row = result.FirstOrDefault();
            
            if (row == null) return null;
            
            return MapRowToConversation(row);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting conversation: {ConversationId}", conversationId);
            throw;
        }
    }

    public async Task<Conversation?> GetPrivateConversationAsync(Guid organizationId, Guid userId1, Guid userId2)
    {
        try
        {
            // Criar user_pair ordenado (para garantir consistência)
            var userPair = CreateUserPair(userId1, userId2);
            
            var bound = _selectPrivateConversationStmt.Value.Bind(organizationId, userPair);
            var result = await _session.ExecuteAsync(bound);
            var row = result.FirstOrDefault();
            
            if (row == null) return null;
            
            var conversationId = row.GetValue<Guid>("conversation_id");
            return await GetByIdAsync(conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting private conversation between {User1} and {User2}", userId1, userId2);
            throw;
        }
    }

    public async Task<IEnumerable<UserConversation>> GetUserConversationsAsync(Guid userId, int limit = 50)
    {
        try
        {
            var bound = _selectUserConversationsStmt.Value.Bind(userId, limit);
            var result = await _session.ExecuteAsync(bound);
            
            var conversations = new List<UserConversation>();
            foreach (var row in result)
            {
                conversations.Add(new UserConversation
                {
                    UserId = userId,
                    ConversationId = row.GetValue<Guid>("conversation_id"),
                    Type = row.GetValue<string>("type") ?? "private",
                    Name = row.GetValue<string>("name"),
                    AvatarUrl = row.GetValue<string>("avatar_url"),
                    LastMessagePreview = row.GetValue<string>("last_message_preview"),
                    LastMessageSender = row.GetValue<string>("last_message_sender"),
                    LastMessageAt = row.GetValue<DateTimeOffset>("last_message_at"),
                    UnreadCount = row.GetValue<int>("unread_count"),
                    IsMuted = row.GetValue<bool>("is_muted"),
                    IsPinned = row.GetValue<bool>("is_pinned")
                });
            }
            
            return conversations;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user conversations: {UserId}", userId);
            throw;
        }
    }

    public async Task AddMemberAsync(ConversationMember member)
    {
        try
        {
            if (member.JoinedAt == default)
            {
                member.JoinedAt = DateTimeOffset.UtcNow;
            }
            
            var bound = _insertMemberStmt.Value.Bind(
                member.ConversationId,
                member.UserId,
                member.Role,
                member.JoinedAt.UtcDateTime,
                member.AddedBy,
                member.Nickname,
                member.NotificationsEnabled
            );
            
            await _session.ExecuteAsync(bound);
            
            _logger.LogInformation(
                "Member added: ConversationId={ConversationId}, UserId={UserId}, Role={Role}",
                member.ConversationId, member.UserId, member.Role);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding member to conversation");
            throw;
        }
    }

    public async Task RemoveMemberAsync(Guid conversationId, Guid userId)
    {
        try
        {
            var bound = _deleteMemberStmt.Value.Bind(conversationId, userId);
            await _session.ExecuteAsync(bound);
            
            _logger.LogInformation(
                "Member removed: ConversationId={ConversationId}, UserId={UserId}",
                conversationId, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing member from conversation");
            throw;
        }
    }

    public async Task<IEnumerable<ConversationMember>> GetMembersAsync(Guid conversationId)
    {
        try
        {
            var bound = _selectMembersStmt.Value.Bind(conversationId);
            var result = await _session.ExecuteAsync(bound);
            
            var members = new List<ConversationMember>();
            foreach (var row in result)
            {
                members.Add(MapRowToMember(row));
            }
            
            return members;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting conversation members: {ConversationId}", conversationId);
            throw;
        }
    }

    public async Task<bool> IsMemberAsync(Guid conversationId, Guid userId)
    {
        try
        {
            var bound = _selectMemberStmt.Value.Bind(conversationId, userId);
            var result = await _session.ExecuteAsync(bound);
            return result.FirstOrDefault() != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking membership: {ConversationId}, {UserId}", conversationId, userId);
            throw;
        }
    }

    public async Task UpdateLastMessageAsync(Guid userId, Guid conversationId, string preview, string senderName)
    {
        try
        {
            // Primeiro, buscar a conversa atual para pegar o tipo e nome
            var conversations = await GetUserConversationsAsync(userId, 100);
            var existingConv = conversations.FirstOrDefault(c => c.ConversationId == conversationId);
            
            var now = DateTimeOffset.UtcNow;
            
            // Se existir, precisamos deletar o registro antigo (porque last_message_at é parte da PK)
            if (existingConv != null)
            {
                var deleteBound = _deleteUserConversationStmt.Value.Bind(
                    userId, 
                    existingConv.LastMessageAt.UtcDateTime, 
                    conversationId);
                await _session.ExecuteAsync(deleteBound);
            }
            
            // Inserir novo registro com timestamp atualizado
            var insertBound = _insertUserConversationStmt.Value.Bind(
                userId,
                now.UtcDateTime,
                conversationId,
                existingConv?.Type ?? "private",
                existingConv?.Name,
                existingConv?.AvatarUrl,
                preview,
                senderName,
                (existingConv?.UnreadCount ?? 0) + 1,
                existingConv?.IsMuted ?? false,
                existingConv?.IsPinned ?? false
            );
            
            await _session.ExecuteAsync(insertBound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating last message");
            throw;
        }
    }

    public async Task IncrementUnreadCountAsync(Guid userId, Guid conversationId)
    {
        // Implementado junto com UpdateLastMessageAsync
        await Task.CompletedTask;
    }

    public async Task ResetUnreadCountAsync(Guid userId, Guid conversationId)
    {
        try
        {
            var conversations = await GetUserConversationsAsync(userId, 100);
            var existingConv = conversations.FirstOrDefault(c => c.ConversationId == conversationId);
            
            if (existingConv == null) return;
            
            // Deletar e reinserir com unread_count = 0
            var deleteBound = _deleteUserConversationStmt.Value.Bind(
                userId, 
                existingConv.LastMessageAt.UtcDateTime, 
                conversationId);
            await _session.ExecuteAsync(deleteBound);
            
            var insertBound = _insertUserConversationStmt.Value.Bind(
                userId,
                existingConv.LastMessageAt.UtcDateTime,
                conversationId,
                existingConv.Type,
                existingConv.Name,
                existingConv.AvatarUrl,
                existingConv.LastMessagePreview,
                existingConv.LastMessageSender,
                0, // Reset unread
                existingConv.IsMuted,
                existingConv.IsPinned
            );
            
            await _session.ExecuteAsync(insertBound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting unread count");
            throw;
        }
    }

    public async Task<Conversation> UpdateAsync(Conversation conversation)
    {
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        
        var bound = _insertConversationStmt.Value.Bind(
            conversation.ConversationId,
            conversation.OrganizationId,
            conversation.Type,
            conversation.Name,
            conversation.Description,
            conversation.AvatarUrl,
            conversation.CreatedBy,
            conversation.CreatedAt.UtcDateTime,
            conversation.UpdatedAt.UtcDateTime,
            conversation.Metadata
        );
        
        await _session.ExecuteAsync(bound);
        return conversation;
    }

    /// <summary>
    /// Registra uma conversa privada no índice de lookup
    /// </summary>
    public async Task RegisterPrivateConversationAsync(Guid organizationId, Guid userId1, Guid userId2, Guid conversationId)
    {
        try
        {
            var userPair = CreateUserPair(userId1, userId2);
            
            var bound = _insertPrivateConversationStmt.Value.Bind(
                organizationId,
                userPair,
                conversationId,
                DateTimeOffset.UtcNow.UtcDateTime
            );
            
            await _session.ExecuteAsync(bound);
            
            _logger.LogInformation(
                "Private conversation registered: {UserPair} -> {ConversationId}",
                userPair, conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering private conversation");
            throw;
        }
    }

    /// <summary>
    /// Adiciona uma conversa à lista de conversas do usuário
    /// </summary>
    public async Task AddUserConversationAsync(UserConversation userConv)
    {
        try
        {
            if (userConv.LastMessageAt == default)
            {
                userConv.LastMessageAt = DateTimeOffset.UtcNow;
            }
            
            var bound = _insertUserConversationStmt.Value.Bind(
                userConv.UserId,
                userConv.LastMessageAt.UtcDateTime,
                userConv.ConversationId,
                userConv.Type,
                userConv.Name,
                userConv.AvatarUrl,
                userConv.LastMessagePreview,
                userConv.LastMessageSender,
                userConv.UnreadCount,
                userConv.IsMuted,
                userConv.IsPinned
            );
            
            await _session.ExecuteAsync(bound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding user conversation");
            throw;
        }
    }

    // Helpers
    private static string CreateUserPair(Guid userId1, Guid userId2)
    {
        // Ordenar para garantir consistência
        var id1 = userId1.ToString();
        var id2 = userId2.ToString();
        return string.Compare(id1, id2, StringComparison.Ordinal) < 0
            ? $"{id1}:{id2}"
            : $"{id2}:{id1}";
    }

    private static Conversation MapRowToConversation(Cassandra.Row row)
    {
        return new Conversation
        {
            ConversationId = row.GetValue<Guid>("conversation_id"),
            OrganizationId = row.GetValue<Guid>("organization_id"),
            Type = row.GetValue<string>("type") ?? "private",
            Name = row.GetValue<string>("name"),
            Description = row.GetValue<string>("description"),
            AvatarUrl = row.GetValue<string>("avatar_url"),
            CreatedBy = row.GetValue<Guid>("created_by"),
            CreatedAt = row.GetValue<DateTimeOffset>("created_at"),
            UpdatedAt = row.GetValue<DateTimeOffset>("updated_at"),
            Metadata = row.GetValue<string>("metadata")
        };
    }

    private static ConversationMember MapRowToMember(Cassandra.Row row)
    {
        return new ConversationMember
        {
            ConversationId = row.GetValue<Guid>("conversation_id"),
            UserId = row.GetValue<Guid>("user_id"),
            Role = row.GetValue<string>("role") ?? "member",
            JoinedAt = row.GetValue<DateTimeOffset>("joined_at"),
            AddedBy = row.GetValue<Guid?>("added_by"),
            Nickname = row.GetValue<string>("nickname"),
            NotificationsEnabled = row.GetValue<bool>("notifications_enabled")
        };
    }
}
