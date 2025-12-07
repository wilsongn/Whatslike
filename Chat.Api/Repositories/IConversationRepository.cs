// Chat.Api/Repositories/IConversationRepository.cs
using Chat.Api.Models;

namespace Chat.Api.Repositories;

public interface IConversationRepository
{
    /// <summary>
    /// Cria uma nova conversa
    /// </summary>
    Task<Conversation> CreateAsync(Conversation conversation);
    
    /// <summary>
    /// Busca uma conversa por ID
    /// </summary>
    Task<Conversation?> GetByIdAsync(Guid conversationId);
    
    /// <summary>
    /// Busca conversa privada entre dois usuários
    /// </summary>
    Task<Conversation?> GetPrivateConversationAsync(Guid organizationId, Guid userId1, Guid userId2);
    
    /// <summary>
    /// Lista todas as conversas de um usuário
    /// </summary>
    Task<IEnumerable<UserConversation>> GetUserConversationsAsync(Guid userId, int limit = 50);
    
    /// <summary>
    /// Adiciona um membro à conversa
    /// </summary>
    Task AddMemberAsync(ConversationMember member);
    
    /// <summary>
    /// Remove um membro da conversa
    /// </summary>
    Task RemoveMemberAsync(Guid conversationId, Guid userId);
    
    /// <summary>
    /// Lista membros de uma conversa
    /// </summary>
    Task<IEnumerable<ConversationMember>> GetMembersAsync(Guid conversationId);
    
    /// <summary>
    /// Verifica se um usuário é membro de uma conversa
    /// </summary>
    Task<bool> IsMemberAsync(Guid conversationId, Guid userId);
    
    /// <summary>
    /// Atualiza a última mensagem de uma conversa para um usuário
    /// </summary>
    Task UpdateLastMessageAsync(Guid userId, Guid conversationId, string preview, string senderName);
    
    /// <summary>
    /// Incrementa contador de mensagens não lidas
    /// </summary>
    Task IncrementUnreadCountAsync(Guid userId, Guid conversationId);
    
    /// <summary>
    /// Zera contador de mensagens não lidas
    /// </summary>
    Task ResetUnreadCountAsync(Guid userId, Guid conversationId);
    
    /// <summary>
    /// Atualiza dados da conversa
    /// </summary>
    Task<Conversation> UpdateAsync(Conversation conversation);
}
