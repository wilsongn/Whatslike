// Chat.Api/Repositories/IPresenceRepository.cs
using Chat.Api.Models;

namespace Chat.Api.Repositories;

public interface IPresenceRepository
{
    /// <summary>
    /// Marca o usuário como online
    /// </summary>
    Task SetOnlineAsync(Guid userId, string connectionId, string? deviceType = null, string? deviceInfo = null);
    
    /// <summary>
    /// Marca o usuário como offline
    /// </summary>
    Task SetOfflineAsync(Guid userId);
    
    /// <summary>
    /// Atualiza o status do usuário (online, away, busy)
    /// </summary>
    Task UpdateStatusAsync(Guid userId, string status);
    
    /// <summary>
    /// Obtém o status de presença de um usuário
    /// </summary>
    Task<UserPresence?> GetPresenceAsync(Guid userId);
    
    /// <summary>
    /// Obtém o status de presença de múltiplos usuários
    /// </summary>
    Task<Dictionary<Guid, UserPresence>> GetPresenceBatchAsync(IEnumerable<Guid> userIds);
    
    /// <summary>
    /// Verifica se um usuário está online
    /// </summary>
    Task<bool> IsOnlineAsync(Guid userId);
    
    /// <summary>
    /// Atualiza o last_seen do usuário (heartbeat)
    /// </summary>
    Task UpdateLastSeenAsync(Guid userId);
    
    /// <summary>
    /// Remove conexões antigas (cleanup)
    /// </summary>
    Task CleanupStaleConnectionsAsync(TimeSpan timeout);
}
