// Chat.Api/Controllers/ConversationsController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Chat.Api.Models;
using Chat.Api.Repositories;
using System.Security.Claims;

namespace Chat.Api.Controllers;

[ApiController]
[Route("api/v1/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly IConversationRepository _conversationRepository;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(
        IConversationRepository conversationRepository,
        IUserRepository userRepository,
        ILogger<ConversationsController> logger)
    {
        _conversationRepository = conversationRepository;
        _userRepository = userRepository;
        _logger = logger;
    }

    /// <summary>
    /// Cria uma nova conversa (privada ou grupo)
    /// Para conversa privada: envia apenas 1 membro (o destinatário)
    /// Para grupo: envia nome e lista de membros
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateConversation([FromBody] CreateConversationRequest request)
    {
        var userId = GetUserIdFromToken();
        var organizationId = GetOrganizationIdFromToken();

        if (userId == Guid.Empty)
        {
            return Unauthorized(new { error = "Invalid token" });
        }

        // Validar request
        if (request.Members == null || request.Members.Count == 0)
        {
            return BadRequest(new { error = "At least one member is required" });
        }

        var type = request.Type?.ToLowerInvariant() ?? "private";

        // ========== CONVERSA PRIVADA ==========
        if (type == "private")
        {
            if (request.Members.Count != 1)
            {
                return BadRequest(new { error = "Private conversation must have exactly one other member" });
            }

            var otherUserId = request.Members[0];

            // Não pode criar conversa consigo mesmo
            if (otherUserId == userId)
            {
                return BadRequest(new { error = "Cannot create conversation with yourself" });
            }

            // Verificar se o outro usuário existe
            var otherUser = await _userRepository.GetByIdAsync(otherUserId);
            if (otherUser == null)
            {
                return NotFound(new { error = "User not found", userId = otherUserId });
            }

            // Verificar se já existe conversa privada entre os dois
            var existingConversation = await _conversationRepository.GetPrivateConversationAsync(
                organizationId, userId, otherUserId);

            if (existingConversation != null)
            {
                _logger.LogInformation(
                    "Private conversation already exists: {ConversationId}",
                    existingConversation.ConversationId);

                return Ok(new
                {
                    conversationId = existingConversation.ConversationId,
                    type = "private",
                    isNew = false,
                    otherUser = new
                    {
                        userId = otherUser.UserId,
                        username = otherUser.Username,
                        displayName = otherUser.DisplayName,
                        avatarUrl = otherUser.AvatarUrl
                    }
                });
            }

            // Criar nova conversa privada
            var currentUser = await _userRepository.GetByIdAsync(userId);
            
            var conversation = new Conversation
            {
                ConversationId = Guid.NewGuid(),
                OrganizationId = organizationId,
                Type = "private",
                Name = null, // Conversas privadas não têm nome
                CreatedBy = userId
            };

            await _conversationRepository.CreateAsync(conversation);

            // Registrar no índice de conversas privadas
            await ((CassandraConversationRepository)_conversationRepository)
                .RegisterPrivateConversationAsync(organizationId, userId, otherUserId, conversation.ConversationId);

            // Adicionar ambos os usuários como membros
            await _conversationRepository.AddMemberAsync(new ConversationMember
            {
                ConversationId = conversation.ConversationId,
                UserId = userId,
                Role = "member"
            });

            await _conversationRepository.AddMemberAsync(new ConversationMember
            {
                ConversationId = conversation.ConversationId,
                UserId = otherUserId,
                Role = "member"
            });

            // Adicionar à lista de conversas de cada usuário
            await ((CassandraConversationRepository)_conversationRepository).AddUserConversationAsync(new UserConversation
            {
                UserId = userId,
                ConversationId = conversation.ConversationId,
                Type = "private",
                Name = otherUser.DisplayName, // Nome do outro usuário
                AvatarUrl = otherUser.AvatarUrl,
                LastMessagePreview = "Conversa iniciada",
                LastMessageSender = currentUser?.DisplayName ?? "Você"
            });

            await ((CassandraConversationRepository)_conversationRepository).AddUserConversationAsync(new UserConversation
            {
                UserId = otherUserId,
                ConversationId = conversation.ConversationId,
                Type = "private",
                Name = currentUser?.DisplayName ?? "Usuário", // Nome do criador
                AvatarUrl = currentUser?.AvatarUrl,
                LastMessagePreview = "Conversa iniciada",
                LastMessageSender = currentUser?.DisplayName ?? "Usuário"
            });

            _logger.LogInformation(
                "Private conversation created: {ConversationId} between {User1} and {User2}",
                conversation.ConversationId, userId, otherUserId);

            return Ok(new
            {
                conversationId = conversation.ConversationId,
                type = "private",
                isNew = true,
                otherUser = new
                {
                    userId = otherUser.UserId,
                    username = otherUser.Username,
                    displayName = otherUser.DisplayName,
                    avatarUrl = otherUser.AvatarUrl
                }
            });
        }

        // ========== GRUPO ==========
        if (type == "group")
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { error = "Group name is required" });
            }

            var currentUser = await _userRepository.GetByIdAsync(userId);

            var conversation = new Conversation
            {
                ConversationId = Guid.NewGuid(),
                OrganizationId = organizationId,
                Type = "group",
                Name = request.Name,
                Description = request.Description,
                AvatarUrl = request.AvatarUrl,
                CreatedBy = userId
            };

            await _conversationRepository.CreateAsync(conversation);

            // Adicionar criador como owner
            await _conversationRepository.AddMemberAsync(new ConversationMember
            {
                ConversationId = conversation.ConversationId,
                UserId = userId,
                Role = "owner"
            });

            // Adicionar à lista de conversas do criador
            await ((CassandraConversationRepository)_conversationRepository).AddUserConversationAsync(new UserConversation
            {
                UserId = userId,
                ConversationId = conversation.ConversationId,
                Type = "group",
                Name = request.Name,
                AvatarUrl = request.AvatarUrl,
                LastMessagePreview = "Grupo criado",
                LastMessageSender = currentUser?.DisplayName ?? "Você"
            });

            // Adicionar outros membros
            var addedMembers = new List<object>();
            foreach (var memberId in request.Members)
            {
                if (memberId == userId) continue; // Pular o criador

                var member = await _userRepository.GetByIdAsync(memberId);
                if (member == null) continue;

                await _conversationRepository.AddMemberAsync(new ConversationMember
                {
                    ConversationId = conversation.ConversationId,
                    UserId = memberId,
                    Role = "member",
                    AddedBy = userId
                });

                // Adicionar à lista de conversas do membro
                await ((CassandraConversationRepository)_conversationRepository).AddUserConversationAsync(new UserConversation
                {
                    UserId = memberId,
                    ConversationId = conversation.ConversationId,
                    Type = "group",
                    Name = request.Name,
                    AvatarUrl = request.AvatarUrl,
                    LastMessagePreview = $"{currentUser?.DisplayName ?? "Alguém"} criou o grupo",
                    LastMessageSender = currentUser?.DisplayName ?? "Sistema"
                });

                addedMembers.Add(new
                {
                    userId = member.UserId,
                    username = member.Username,
                    displayName = member.DisplayName
                });
            }

            _logger.LogInformation(
                "Group created: {ConversationId}, Name={Name}, Members={MemberCount}",
                conversation.ConversationId, request.Name, addedMembers.Count + 1);

            return Ok(new
            {
                conversationId = conversation.ConversationId,
                type = "group",
                name = request.Name,
                isNew = true,
                members = addedMembers
            });
        }

        return BadRequest(new { error = "Invalid conversation type. Use 'private' or 'group'" });
    }

    /// <summary>
    /// Lista todas as conversas do usuário
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetConversations([FromQuery] int limit = 50)
    {
        var userId = GetUserIdFromToken();

        var conversations = await _conversationRepository.GetUserConversationsAsync(userId, limit);

        return Ok(new
        {
            conversations = conversations.Select(c => new
            {
                conversationId = c.ConversationId,
                type = c.Type,
                name = c.Name,
                avatarUrl = c.AvatarUrl,
                lastMessage = new
                {
                    preview = c.LastMessagePreview,
                    sender = c.LastMessageSender,
                    at = c.LastMessageAt
                },
                unreadCount = c.UnreadCount,
                isMuted = c.IsMuted,
                isPinned = c.IsPinned
            })
        });
    }

    /// <summary>
    /// Obtém detalhes de uma conversa
    /// </summary>
    [HttpGet("{conversationId:guid}")]
    public async Task<IActionResult> GetConversation(Guid conversationId)
    {
        var userId = GetUserIdFromToken();

        // Verificar se é membro
        var isMember = await _conversationRepository.IsMemberAsync(conversationId, userId);
        if (!isMember)
        {
            return Forbid();
        }

        var conversation = await _conversationRepository.GetByIdAsync(conversationId);
        if (conversation == null)
        {
            return NotFound(new { error = "Conversation not found" });
        }

        // Buscar membros com detalhes
        var members = await _conversationRepository.GetMembersAsync(conversationId);
        var memberDetails = new List<object>();

        foreach (var member in members)
        {
            var user = await _userRepository.GetByIdAsync(member.UserId);
            if (user != null)
            {
                memberDetails.Add(new
                {
                    userId = user.UserId,
                    username = user.Username,
                    displayName = user.DisplayName,
                    avatarUrl = user.AvatarUrl,
                    role = member.Role,
                    joinedAt = member.JoinedAt
                });
            }
        }

        return Ok(new
        {
            conversationId = conversation.ConversationId,
            type = conversation.Type,
            name = conversation.Name,
            description = conversation.Description,
            avatarUrl = conversation.AvatarUrl,
            createdBy = conversation.CreatedBy,
            createdAt = conversation.CreatedAt,
            members = memberDetails
        });
    }

    /// <summary>
    /// Obtém histórico de mensagens de uma conversa
    /// </summary>
    [HttpGet("{conversationId:guid}/messages")]
    public async Task<IActionResult> GetMessages(
        Guid conversationId,
        [FromQuery] int limit = 50,
        [FromQuery] DateTimeOffset? before = null)
    {
        var userId = GetUserIdFromToken();

        // Verificar se é membro
        var isMember = await _conversationRepository.IsMemberAsync(conversationId, userId);
        if (!isMember)
        {
            return Forbid();
        }

        // Resetar contador de não lidas ao ver mensagens
        await _conversationRepository.ResetUnreadCountAsync(userId, conversationId);

        // TODO: Implementar busca de mensagens no MessageRepository
        // Por enquanto, retornar array vazio
        // As mensagens estão na tabela 'mensagens' existente

        return Ok(new
        {
            conversationId,
            messages = new List<object>(),
            hasMore = false
        });
    }

    /// <summary>
    /// Adiciona um membro ao grupo
    /// </summary>
    [HttpPost("{conversationId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid conversationId, [FromBody] AddMemberRequest request)
    {
        var userId = GetUserIdFromToken();

        // Verificar se a conversa existe e é um grupo
        var conversation = await _conversationRepository.GetByIdAsync(conversationId);
        if (conversation == null)
        {
            return NotFound(new { error = "Conversation not found" });
        }

        if (conversation.Type != "group")
        {
            return BadRequest(new { error = "Can only add members to groups" });
        }

        // Verificar se o usuário atual é membro (e tem permissão)
        var members = await _conversationRepository.GetMembersAsync(conversationId);
        var currentMember = members.FirstOrDefault(m => m.UserId == userId);
        
        if (currentMember == null)
        {
            return Forbid();
        }

        if (currentMember.Role != "owner" && currentMember.Role != "admin")
        {
            return Forbid();
        }

        // Verificar se o novo membro existe
        var newUser = await _userRepository.GetByIdAsync(request.UserId);
        if (newUser == null)
        {
            return NotFound(new { error = "User not found" });
        }

        // Verificar se já é membro
        var alreadyMember = members.Any(m => m.UserId == request.UserId);
        if (alreadyMember)
        {
            return Conflict(new { error = "User is already a member" });
        }

        // Adicionar membro
        await _conversationRepository.AddMemberAsync(new ConversationMember
        {
            ConversationId = conversationId,
            UserId = request.UserId,
            Role = "member",
            AddedBy = userId
        });

        // Adicionar à lista de conversas do novo membro
        var currentUser = await _userRepository.GetByIdAsync(userId);
        await ((CassandraConversationRepository)_conversationRepository).AddUserConversationAsync(new UserConversation
        {
            UserId = request.UserId,
            ConversationId = conversationId,
            Type = "group",
            Name = conversation.Name,
            AvatarUrl = conversation.AvatarUrl,
            LastMessagePreview = $"{currentUser?.DisplayName ?? "Alguém"} adicionou você ao grupo",
            LastMessageSender = "Sistema"
        });

        _logger.LogInformation(
            "Member added to group: ConversationId={ConversationId}, NewMember={NewMember}, AddedBy={AddedBy}",
            conversationId, request.UserId, userId);

        return Ok(new
        {
            success = true,
            member = new
            {
                userId = newUser.UserId,
                username = newUser.Username,
                displayName = newUser.DisplayName,
                role = "member"
            }
        });
    }

    /// <summary>
    /// Envia uma mensagem em uma conversa
    /// </summary>
    [HttpPost("{conversationId:guid}/messages")]
    public async Task<IActionResult> SendMessage(
        Guid conversationId,
        [FromBody] SendConversationMessageRequest request)
    {
        var userId = GetUserIdFromToken();
        var organizationId = GetOrganizationIdFromToken();

        if (userId == Guid.Empty)
        {
            return Unauthorized(new { error = "Invalid token" });
        }

        // Verificar se é membro da conversa
        var isMember = await _conversationRepository.IsMemberAsync(conversationId, userId);
        if (!isMember)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest(new { error = "Content is required" });
        }

        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Buscar dados do remetente
        var sender = await _userRepository.GetByIdAsync(userId);
        var senderName = sender?.DisplayName ?? "Usuário";

        try
        {
            // Publicar no Redis para os clientes conectados via WebSocket
            var redis = HttpContext.RequestServices.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
            var subscriber = redis.GetSubscriber();

            var messagePayload = new
            {
                type = "message",
                messageId = messageId,
                conversationId = conversationId,
                senderId = userId,
                senderName = senderName,
                content = request.Content,
                contentType = "text",
                createdAt = now
            };

            var json = System.Text.Json.JsonSerializer.Serialize(messagePayload);

            // Publicar no canal da conversa
            await subscriber.PublishAsync(
                StackExchange.Redis.RedisChannel.Literal($"status:{conversationId}"),
                json
            );

            _logger.LogInformation(
                "Message sent: ConversationId={ConversationId}, MessageId={MessageId}",
                conversationId, messageId);

            // Atualizar preview da conversa para todos os membros
            var members = await _conversationRepository.GetMembersAsync(conversationId);

            foreach (var member in members)
            {
                try
                {
                    var preview = request.Content.Length > 50
                        ? request.Content.Substring(0, 47) + "..."
                        : request.Content;

                    await ((CassandraConversationRepository)_conversationRepository).UpdateLastMessageAsync(
                        member.UserId,
                        conversationId,
                        preview,
                        senderName,
                        now
                    );

                    // Incrementar contador de não lidas para outros membros
                    if (member.UserId != userId)
                    {
                        await _conversationRepository.IncrementUnreadCountAsync(member.UserId, conversationId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error updating conversation preview for user {UserId}", member.UserId);
                }
            }

            return Ok(new
            {
                messageId = messageId,
                conversationId = conversationId,
                senderId = userId,
                senderName = senderName,
                content = request.Content,
                createdAt = now,
                status = "sent"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message to conversation {ConversationId}", conversationId);
            return StatusCode(500, new { error = "Failed to send message" });
        }
    }

    /// <summary>
    /// Remove um membro do grupo
    /// </summary>
    [HttpDelete("{conversationId:guid}/members/{memberUserId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid conversationId, Guid memberUserId)
    {
        var userId = GetUserIdFromToken();

        var conversation = await _conversationRepository.GetByIdAsync(conversationId);
        if (conversation == null)
        {
            return NotFound(new { error = "Conversation not found" });
        }

        if (conversation.Type != "group")
        {
            return BadRequest(new { error = "Can only remove members from groups" });
        }

        var members = await _conversationRepository.GetMembersAsync(conversationId);
        var currentMember = members.FirstOrDefault(m => m.UserId == userId);
        var targetMember = members.FirstOrDefault(m => m.UserId == memberUserId);

        if (currentMember == null)
        {
            return Forbid();
        }

        if (targetMember == null)
        {
            return NotFound(new { error = "Member not found" });
        }

        // Pode remover a si mesmo, ou admin/owner pode remover outros
        bool canRemove = memberUserId == userId ||
                        currentMember.Role == "owner" ||
                        (currentMember.Role == "admin" && targetMember.Role == "member");

        if (!canRemove)
        {
            return Forbid();
        }

        // Não pode remover o owner
        if (targetMember.Role == "owner")
        {
            return BadRequest(new { error = "Cannot remove the group owner" });
        }

        await _conversationRepository.RemoveMemberAsync(conversationId, memberUserId);

        _logger.LogInformation(
            "Member removed from group: ConversationId={ConversationId}, Member={Member}",
            conversationId, memberUserId);

        return Ok(new { success = true });
    }

    // Helpers
    private Guid GetUserIdFromToken()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            return userId;
        }
        return Guid.Empty;
    }

    private Guid GetOrganizationIdFromToken()
    {
        var orgIdClaim = User.FindFirst("tenant_id")?.Value
            ?? User.FindFirst("organization_id")?.Value;
        
        if (Guid.TryParse(orgIdClaim, out var orgId))
        {
            return orgId;
        }
        return Guid.Empty;
    }
}

// ============================================
// Request DTOs
// ============================================

public class CreateConversationRequest
{
    /// <summary>
    /// Tipo: 'private' ou 'group'
    /// </summary>
    public string? Type { get; set; } = "private";
    
    /// <summary>
    /// Nome do grupo (obrigatório para grupos)
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// Descrição do grupo (opcional)
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// URL do avatar do grupo (opcional)
    /// </summary>
    public string? AvatarUrl { get; set; }
    
    /// <summary>
    /// Lista de user_ids dos membros
    /// Para private: apenas 1 membro (o destinatário)
    /// Para group: todos os membros iniciais
    /// </summary>
    public List<Guid> Members { get; set; } = new();
}

public class AddMemberRequest
{
    public Guid UserId { get; set; }
}

public class SendConversationMessageRequest
{
    public string Content { get; set; } = "";
}
