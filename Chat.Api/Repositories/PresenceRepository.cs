// Chat.Api/Repositories/PresenceRepository.cs
using Chat.Api.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace Chat.Api.Repositories;

public class RedisPresenceRepository : IPresenceRepository
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisPresenceRepository> _logger;
    
    // TTL para presença: 60 segundos (heartbeat deve renovar)
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(60);
    
    // Prefixos das chaves
    private const string PresenceKeyPrefix = "presence:";
    private const string OnlineSetKey = "online_users";
    
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RedisPresenceRepository(IConnectionMultiplexer redis, ILogger<RedisPresenceRepository> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task SetOnlineAsync(Guid userId, string connectionId, string? deviceType = null, string? deviceInfo = null)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = GetPresenceKey(userId);
            var now = DateTimeOffset.UtcNow;
            
            var presence = new UserPresence
            {
                UserId = userId,
                Status = "online",
                LastSeen = now,
                ConnectionId = connectionId,
                DeviceType = deviceType ?? "web",
                DeviceInfo = deviceInfo
            };
            
            var json = JsonSerializer.Serialize(presence, _jsonOptions);
            
            // Usar transaction para atomicidade
            var transaction = db.CreateTransaction();
            
            // Salvar dados de presença com TTL
            _ = transaction.StringSetAsync(key, json, PresenceTtl);
            
            // Adicionar ao set de usuários online
            _ = transaction.SetAddAsync(OnlineSetKey, userId.ToString());
            
            await transaction.ExecuteAsync();
            
            _logger.LogInformation(
                "User online (Redis): UserId={UserId}, ConnectionId={ConnectionId}",
                userId, connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting user online: {UserId}", userId);
            throw;
        }
    }

    public async Task SetOfflineAsync(Guid userId)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = GetPresenceKey(userId);
            
            var transaction = db.CreateTransaction();
            
            // Remover dados de presença
            _ = transaction.KeyDeleteAsync(key);
            
            // Remover do set de usuários online
            _ = transaction.SetRemoveAsync(OnlineSetKey, userId.ToString());
            
            await transaction.ExecuteAsync();
            
            _logger.LogInformation("User offline (Redis): UserId={UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting user offline: {UserId}", userId);
            throw;
        }
    }

    public async Task UpdateStatusAsync(Guid userId, string status)
    {
        try
        {
            var validStatuses = new[] { "online", "away", "busy" };
            if (!validStatuses.Contains(status.ToLowerInvariant()))
            {
                throw new ArgumentException($"Invalid status: {status}");
            }
            
            var db = _redis.GetDatabase();
            var key = GetPresenceKey(userId);
            
            // Buscar presença atual
            var existing = await db.StringGetAsync(key);
            if (existing.IsNullOrEmpty)
            {
                _logger.LogWarning("Cannot update status for offline user: {UserId}", userId);
                return;
            }
            
            var presence = JsonSerializer.Deserialize<UserPresence>(existing!, _jsonOptions);
            if (presence == null) return;
            
            presence.Status = status.ToLowerInvariant();
            presence.LastSeen = DateTimeOffset.UtcNow;
            
            var json = JsonSerializer.Serialize(presence, _jsonOptions);
            
            // Atualizar com novo TTL
            await db.StringSetAsync(key, json, PresenceTtl);
            
            _logger.LogInformation(
                "User status updated (Redis): UserId={UserId}, Status={Status}",
                userId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating user status: {UserId}", userId);
            throw;
        }
    }

    public async Task<UserPresence?> GetPresenceAsync(Guid userId)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = GetPresenceKey(userId);
            
            var json = await db.StringGetAsync(key);
            
            if (json.IsNullOrEmpty)
            {
                // Usuário offline (chave expirou ou não existe)
                return new UserPresence
                {
                    UserId = userId,
                    Status = "offline",
                    LastSeen = DateTimeOffset.MinValue
                };
            }
            
            return JsonSerializer.Deserialize<UserPresence>(json!, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting user presence: {UserId}", userId);
            throw;
        }
    }

    public async Task<Dictionary<Guid, UserPresence>> GetPresenceBatchAsync(IEnumerable<Guid> userIds)
    {
        var result = new Dictionary<Guid, UserPresence>();
        
        try
        {
            var db = _redis.GetDatabase();
            var userIdList = userIds.ToList();
            
            // Buscar todas as chaves de uma vez
            var keys = userIdList.Select(id => (RedisKey)GetPresenceKey(id)).ToArray();
            var values = await db.StringGetAsync(keys);
            
            for (int i = 0; i < userIdList.Count; i++)
            {
                var userId = userIdList[i];
                var json = values[i];
                
                if (!json.IsNullOrEmpty)
                {
                    var presence = JsonSerializer.Deserialize<UserPresence>(json!, _jsonOptions);
                    if (presence != null)
                    {
                        result[userId] = presence;
                    }
                }
                else
                {
                    // Usuário offline
                    result[userId] = new UserPresence
                    {
                        UserId = userId,
                        Status = "offline",
                        LastSeen = DateTimeOffset.MinValue
                    };
                }
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting batch presence");
            throw;
        }
    }

    public async Task<bool> IsOnlineAsync(Guid userId)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = GetPresenceKey(userId);
            
            // Se a chave existe, o usuário está online (TTL garante isso)
            return await db.KeyExistsAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if user is online: {UserId}", userId);
            return false;
        }
    }

    public async Task UpdateLastSeenAsync(Guid userId)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = GetPresenceKey(userId);
            
            // Buscar presença atual
            var existing = await db.StringGetAsync(key);
            if (existing.IsNullOrEmpty)
            {
                // Usuário não está online, ignorar heartbeat
                return;
            }
            
            var presence = JsonSerializer.Deserialize<UserPresence>(existing!, _jsonOptions);
            if (presence == null) return;
            
            presence.LastSeen = DateTimeOffset.UtcNow;
            
            var json = JsonSerializer.Serialize(presence, _jsonOptions);
            
            // Renovar TTL
            await db.StringSetAsync(key, json, PresenceTtl);
            
            _logger.LogDebug("Heartbeat updated: UserId={UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error updating last seen: {UserId}", userId);
            // Não propagar erro de heartbeat
        }
    }

    public async Task CleanupStaleConnectionsAsync(TimeSpan timeout)
    {
        // Com Redis e TTL, isso é automático!
        // Mas podemos limpar o set de usuários online para manter consistência
        try
        {
            var db = _redis.GetDatabase();
            var members = await db.SetMembersAsync(OnlineSetKey);
            
            foreach (var member in members)
            {
                if (Guid.TryParse(member, out var userId))
                {
                    var key = GetPresenceKey(userId);
                    var exists = await db.KeyExistsAsync(key);
                    
                    if (!exists)
                    {
                        // Remover do set se a chave de presença expirou
                        await db.SetRemoveAsync(OnlineSetKey, member);
                        _logger.LogDebug("Removed stale user from online set: {UserId}", userId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up stale connections");
        }
    }

    /// <summary>
    /// Obtém lista de todos os usuários online
    /// </summary>
    public async Task<List<Guid>> GetAllOnlineUsersAsync()
    {
        try
        {
            var db = _redis.GetDatabase();
            var members = await db.SetMembersAsync(OnlineSetKey);
            
            var onlineUsers = new List<Guid>();
            foreach (var member in members)
            {
                if (Guid.TryParse(member, out var userId))
                {
                    // Verificar se realmente está online (chave existe)
                    if (await db.KeyExistsAsync(GetPresenceKey(userId)))
                    {
                        onlineUsers.Add(userId);
                    }
                }
            }
            
            return onlineUsers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all online users");
            return new List<Guid>();
        }
    }

    /// <summary>
    /// Obtém o número de usuários online
    /// </summary>
    public async Task<long> GetOnlineCountAsync()
    {
        try
        {
            var db = _redis.GetDatabase();
            return await db.SetLengthAsync(OnlineSetKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting online count");
            return 0;
        }
    }

    private static string GetPresenceKey(Guid userId) => $"{PresenceKeyPrefix}{userId}";
}
