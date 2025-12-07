// Chat.Api/Repositories/IPendingMessageRepository.cs
using Chat.Api.Models;

namespace Chat.Api.Repositories;

public interface IPendingMessageRepository
{
    /// <summary>
    /// Salva uma mensagem pendente para um usuário offline
    /// </summary>
    Task SaveAsync(PendingMessage message);
    
    /// <summary>
    /// Salva mensagens pendentes para múltiplos usuários offline
    /// </summary>
    Task SaveBatchAsync(IEnumerable<PendingMessage> messages);
    
    /// <summary>
    /// Busca todas as mensagens pendentes de um usuário
    /// </summary>
    Task<IEnumerable<PendingMessage>> GetPendingMessagesAsync(Guid userId, int limit = 100);
    
    /// <summary>
    /// Remove uma mensagem pendente após entrega
    /// </summary>
    Task DeleteAsync(Guid userId, DateTimeOffset createdAt, Guid messageId);
    
    /// <summary>
    /// Remove todas as mensagens pendentes de um usuário
    /// </summary>
    Task DeleteAllAsync(Guid userId);
    
    /// <summary>
    /// Remove mensagens pendentes específicas (após confirmação de recebimento)
    /// </summary>
    Task DeleteBatchAsync(Guid userId, IEnumerable<Guid> messageIds);
    
    /// <summary>
    /// Conta mensagens pendentes de um usuário
    /// </summary>
    Task<int> CountAsync(Guid userId);
}
