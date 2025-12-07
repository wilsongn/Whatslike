// Chat.Api/Services/PresenceService.cs
using Chat.Api.Models;
using Chat.Api.Repositories;
using StackExchange.Redis;
using System.Text.Json;

namespace Chat.Api.Services;

public interface IPresenceService
{
    /// <summary>
    /// Chamado quando um usuário conecta no WebSocket
    /// </summary>
    Task OnUserConnectedAsync(Guid userId, string connectionId, string? deviceType = null);

    /// <summary>
    /// Chamado quando um usuário desconecta do WebSocket
    /// </summary>
    Task OnUserDisconnectedAsync(Guid userId, string connectionId);

    /// <summary>
    /// Verifica se um usuário está online
    /// </summary>
    Task<bool> IsUserOnlineAsync(Guid userId);

    /// <summary>
    /// Obtém o status de presença de um usuário
    /// </summary>
    Task<UserPresence?> GetUserPresenceAsync(Guid userId);

    /// <summary>
    /// Obtém lista de usuários online de uma lista
    /// </summary>
    Task<List<Guid>> GetOnlineUsersAsync(IEnumerable<Guid> userIds);

    /// <summary>
    /// Notifica outros usuários sobre mudança de presença
    /// </summary>
    Task BroadcastPresenceChangeAsync(Guid userId, string status);
}

public class PresenceService : IPresenceService
{
    private readonly IPresenceRepository _presenceRepository;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<PresenceService> _logger;
    private readonly IServiceProvider _serviceProvider;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PresenceService(
        IPresenceRepository presenceRepository,
        IConnectionMultiplexer redis,
        ILogger<PresenceService> logger,
        IServiceProvider serviceProvider)
    {
        _presenceRepository = presenceRepository;
        _redis = redis;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task OnUserConnectedAsync(Guid userId, string connectionId, string? deviceType = null)
    {
        try
        {
            // Marcar como online no Redis
            await _presenceRepository.SetOnlineAsync(userId, connectionId, deviceType);

            // Notificar outros usuários via Redis
            await BroadcastPresenceChangeAsync(userId, "online");

            // Entregar mensagens pendentes (resolver via ServiceProvider para evitar dependência circular)
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var offlineMessageService = scope.ServiceProvider.GetService<IOfflineMessageService>();
                if (offlineMessageService != null)
                {
                    await offlineMessageService.DeliverPendingMessagesAsync(userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error delivering pending messages on connect: {UserId}", userId);
            }

            _logger.LogInformation(
                "User connected: UserId={UserId}, ConnectionId={ConnectionId}",
                userId, connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling user connection: {UserId}", userId);
        }
    }

    public async Task OnUserDisconnectedAsync(Guid userId, string connectionId)
    {
        try
        {
            // Verificar se é a conexão atual (evita desconectar se reconectou em outra aba)
            var currentPresence = await _presenceRepository.GetPresenceAsync(userId);

            if (currentPresence?.ConnectionId == connectionId)
            {
                // Marcar como offline no Redis
                await _presenceRepository.SetOfflineAsync(userId);

                // Notificar outros usuários via Redis
                await BroadcastPresenceChangeAsync(userId, "offline");

                _logger.LogInformation(
                    "User disconnected: UserId={UserId}, ConnectionId={ConnectionId}",
                    userId, connectionId);
            }
            else
            {
                _logger.LogInformation(
                    "User has newer connection, not marking offline: UserId={UserId}",
                    userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling user disconnection: {UserId}", userId);
        }
    }

    public async Task<bool> IsUserOnlineAsync(Guid userId)
    {
        return await _presenceRepository.IsOnlineAsync(userId);
    }

    public async Task<UserPresence?> GetUserPresenceAsync(Guid userId)
    {
        return await _presenceRepository.GetPresenceAsync(userId);
    }

    public async Task<List<Guid>> GetOnlineUsersAsync(IEnumerable<Guid> userIds)
    {
        var onlineUsers = new List<Guid>();

        var presences = await _presenceRepository.GetPresenceBatchAsync(userIds);

        foreach (var (userId, presence) in presences)
        {
            if (presence.Status == "online" &&
                presence.LastSeen > DateTimeOffset.UtcNow.AddMinutes(-5))
            {
                onlineUsers.Add(userId);
            }
        }

        return onlineUsers;
    }

    public async Task BroadcastPresenceChangeAsync(Guid userId, string status)
    {
        try
        {
            var subscriber = _redis.GetSubscriber();

            var message = new
            {
                type = "presence.change",
                userId,
                status,
                timestamp = DateTimeOffset.UtcNow
            };

            var json = JsonSerializer.Serialize(message, _jsonOptions);

            // Publicar em um canal global de presença
            await subscriber.PublishAsync(
                RedisChannel.Literal("presence:changes"),
                json
            );

            _logger.LogDebug(
                "Presence change broadcasted: UserId={UserId}, Status={Status}",
                userId, status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error broadcasting presence change");
        }
    }
}