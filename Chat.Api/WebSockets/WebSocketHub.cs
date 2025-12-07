using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;

namespace Chat.Api.WebSockets;

/// <summary>
/// Gerencia conexões WebSocket dos clientes
/// </summary>
public class WebSocketHub : IDisposable
{
    private readonly ILogger<WebSocketHub> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly ConcurrentDictionary<string, ClientConnection> _connections;
    private readonly ConcurrentDictionary<string, HashSet<string>> _conversationSubscriptions; // Mudado para string key
    private ISubscriber? _subscriber;
    private bool _isSubscribing;

    // Opções de deserialização case-insensitive
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public WebSocketHub(ILogger<WebSocketHub> logger, IConnectionMultiplexer redis)
    {
        _logger = logger;
        _redis = redis;
        _connections = new ConcurrentDictionary<string, ClientConnection>();
        _conversationSubscriptions = new ConcurrentDictionary<string, HashSet<string>>();
        _subscriber = null;
        _isSubscribing = false;
    }

    public async Task HandleConnectionAsync(
        WebSocket webSocket,
        Guid userId,
        Guid? organizacaoId,
        CancellationToken ct)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var connection = new ClientConnection(connectionId, webSocket, userId, organizacaoId ?? Guid.Empty);

        _connections.TryAdd(connectionId, connection);
        _logger.LogInformation("WebSocket conectado: ConnectionId={ConnectionId} UserId={UserId}",
            connectionId, userId);

        try
        {
            // Enviar mensagem de boas-vindas
            await SendToClientAsync(connection, new
            {
                type = "connected",
                connectionId,
                userId = userId.ToString(),
                timestamp = DateTimeOffset.UtcNow
            }, ct);

            // Loop de recebimento de mensagens do cliente
            var buffer = new byte[1024 * 4];
            while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Fechado pelo cliente",
                        ct);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleClientMessageAsync(connection, message, ct);
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogInformation("WebSocket fechado prematuramente: ConnectionId={ConnectionId}", connectionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Operação cancelada para ConnectionId={ConnectionId}", connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro no WebSocket: ConnectionId={ConnectionId}", connectionId);
        }
        finally
        {
            // Cleanup
            _connections.TryRemove(connectionId, out _);

            // Remover de todas as subscriptions
            foreach (var (conversationId, subscribers) in _conversationSubscriptions)
            {
                lock (subscribers)
                {
                    subscribers.Remove(connectionId);
                }
                if (subscribers.Count == 0)
                {
                    _conversationSubscriptions.TryRemove(conversationId, out _);
                }
            }

            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Conexão encerrada",
                        CancellationToken.None);
                }
                catch { }
            }

            webSocket.Dispose();
            _logger.LogInformation("WebSocket desconectado: ConnectionId={ConnectionId}", connectionId);
        }
    }

    private async Task HandleClientMessageAsync(ClientConnection connection, string message, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Mensagem raw recebida: {Message}", message);

            var clientMessage = JsonSerializer.Deserialize<ClientMessage>(message, _jsonOptions);
            if (clientMessage == null)
            {
                _logger.LogWarning("Mensagem deserializada como null");
                return;
            }

            _logger.LogInformation("Mensagem recebida: Type={Type} ConversationId={ConversationId} ConnectionId={ConnectionId}",
                clientMessage.Type, clientMessage.ConversationId, connection.ConnectionId);

            var messageType = clientMessage.Type?.ToLower();

            switch (messageType)
            {
                case "subscribe":
                    await HandleSubscribeAsync(connection, clientMessage, ct);
                    break;

                case "unsubscribe":
                    await HandleUnsubscribeAsync(connection, clientMessage, ct);
                    break;

                case "ping":
                    await SendToClientAsync(connection, new { type = "pong", timestamp = DateTimeOffset.UtcNow }, ct);
                    break;

                default:
                    _logger.LogWarning("Tipo de mensagem desconhecido: {Type} - Raw: {Raw}", clientMessage.Type, message);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Erro ao deserializar mensagem do cliente: {Message}", message);
            await SendToClientAsync(connection, new
            {
                type = "error",
                message = "Mensagem inválida"
            }, ct);
        }
    }

    private async Task HandleSubscribeAsync(ClientConnection connection, ClientMessage message, CancellationToken ct)
    {
        var conversationId = message.ConversationId;

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            await SendToClientAsync(connection, new
            {
                type = "error",
                message = "ConversationId é obrigatório"
            }, ct);
            return;
        }

        // Aceitar tanto GUID quanto string normal
        // Se for GUID, normalizar para formato padrão
        if (Guid.TryParse(conversationId, out var guidValue))
        {
            conversationId = guidValue.ToString();
        }

        // Adicionar conexão às subscriptions da conversa
        var subscribers = _conversationSubscriptions.GetOrAdd(conversationId, _ => new HashSet<string>());
        lock (subscribers)
        {
            subscribers.Add(connection.ConnectionId);
        }

        // Iniciar Redis subscription se ainda não estiver ativo
        if (!_isSubscribing)
        {
            await StartRedisSubscriptionAsync();
        }

        await SendToClientAsync(connection, new
        {
            type = "subscribed",
            conversationId = conversationId,
            timestamp = DateTimeOffset.UtcNow
        }, ct);

        _logger.LogInformation("Cliente inscrito: ConnectionId={ConnectionId} ConversationId={ConversationId}",
            connection.ConnectionId, conversationId);
    }

    private async Task HandleUnsubscribeAsync(ClientConnection connection, ClientMessage message, CancellationToken ct)
    {
        var conversationId = message.ConversationId;

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        // Normalizar GUID se necessário
        if (Guid.TryParse(conversationId, out var guidValue))
        {
            conversationId = guidValue.ToString();
        }

        if (_conversationSubscriptions.TryGetValue(conversationId, out var subscribers))
        {
            lock (subscribers)
            {
                subscribers.Remove(connection.ConnectionId);
            }

            if (subscribers.Count == 0)
            {
                _conversationSubscriptions.TryRemove(conversationId, out _);
            }
        }

        await SendToClientAsync(connection, new
        {
            type = "unsubscribed",
            conversationId = conversationId,
            timestamp = DateTimeOffset.UtcNow
        }, ct);

        _logger.LogInformation("Cliente desinscrito: ConnectionId={ConnectionId} ConversationId={ConversationId}",
            connection.ConnectionId, conversationId);
    }

    private async Task StartRedisSubscriptionAsync()
    {
        if (_isSubscribing) return;

        _isSubscribing = true;
        _subscriber = _redis.GetSubscriber();

        // Subscribe a todos os canais de status (pattern matching)
        await _subscriber.SubscribeAsync(
            RedisChannel.Pattern("status:*"),
            async (channel, value) =>
            {
                try
                {
                    var channelName = channel.ToString();
                    // Extrair conversationId do canal "status:{id}"
                    var parts = channelName.Split(':');
                    if (parts.Length != 2)
                    {
                        return;
                    }

                    var conversationId = parts[1];

                    if (!_conversationSubscriptions.TryGetValue(conversationId, out var subscribers))
                    {
                        _logger.LogDebug("Nenhum cliente inscrito na conversa: {ConversationId}", conversationId);
                        return;
                    }

                    var notification = value.ToString();
                    _logger.LogInformation("Notificação Redis recebida: Channel={Channel} Subscribers={Count}",
                        channelName, subscribers.Count);

                    // Enviar para todos os clientes inscritos
                    var tasks = new List<Task>();
                    lock (subscribers)
                    {
                        foreach (var connectionId in subscribers.ToList())
                        {
                            if (_connections.TryGetValue(connectionId, out var connection))
                            {
                                tasks.Add(SendToClientAsync(connection, notification, CancellationToken.None));
                            }
                        }
                    }

                    await Task.WhenAll(tasks);
                    _logger.LogInformation("Notificação enviada para {Count} clientes", tasks.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao processar notificação Redis");
                }
            });

        // Também subscribe para mensagens novas
        await _subscriber.SubscribeAsync(
            RedisChannel.Pattern("messages:*"),
            async (channel, value) =>
            {
                try
                {
                    var channelName = channel.ToString();
                    var parts = channelName.Split(':');
                    if (parts.Length != 2) return;

                    var conversationId = parts[1];

                    if (!_conversationSubscriptions.TryGetValue(conversationId, out var subscribers))
                    {
                        return;
                    }

                    var notification = value.ToString();
                    _logger.LogInformation("Nova mensagem Redis: Channel={Channel}", channelName);

                    var tasks = new List<Task>();
                    lock (subscribers)
                    {
                        foreach (var connectionId in subscribers.ToList())
                        {
                            if (_connections.TryGetValue(connectionId, out var connection))
                            {
                                tasks.Add(SendToClientAsync(connection, notification, CancellationToken.None));
                            }
                        }
                    }

                    await Task.WhenAll(tasks);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao processar mensagem Redis");
                }
            });

        _logger.LogInformation("Redis subscription iniciada para patterns: status:* e messages:*");
    }

    /// <summary>
    /// Broadcast uma mensagem para todos os clientes inscritos em uma conversa
    /// </summary>
    public async Task BroadcastToConversationAsync(string conversationId, object message)
    {
        if (!_conversationSubscriptions.TryGetValue(conversationId, out var subscribers))
        {
            _logger.LogDebug("Nenhum subscriber para conversa: {ConversationId}", conversationId);
            return;
        }

        var tasks = new List<Task>();
        lock (subscribers)
        {
            foreach (var connectionId in subscribers.ToList())
            {
                if (_connections.TryGetValue(connectionId, out var connection))
                {
                    tasks.Add(SendToClientAsync(connection, message, CancellationToken.None));
                }
            }
        }

        await Task.WhenAll(tasks);
        _logger.LogInformation("Broadcast para conversa {ConversationId}: {Count} clientes",
            conversationId, tasks.Count);
    }

    private async Task SendToClientAsync(ClientConnection connection, object data, CancellationToken ct)
    {
        try
        {
            if (connection.WebSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning("WebSocket não está aberto: ConnectionId={ConnectionId} State={State}",
                    connection.ConnectionId, connection.WebSocket.State);
                return;
            }

            var json = data is string str ? str : JsonSerializer.Serialize(data, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await connection.WebSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);

            _logger.LogDebug("Mensagem enviada para ConnectionId={ConnectionId}: {Json}",
                connection.ConnectionId, json.Length > 100 ? json.Substring(0, 100) + "..." : json);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "Erro ao enviar para cliente: ConnectionId={ConnectionId}",
                connection.ConnectionId);
        }
    }

    public void Dispose()
    {
        _subscriber?.UnsubscribeAll();
        foreach (var connection in _connections.Values)
        {
            try { connection.WebSocket.Dispose(); } catch { }
        }
        _connections.Clear();
    }

    private record ClientConnection(
        string ConnectionId,
        WebSocket WebSocket,
        Guid UserId,
        Guid OrganizacaoId);

    private class ClientMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("conversationId")]
        public string? ConversationId { get; set; }
    }
}