using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _conversationSubscriptions;
    private ISubscriber? _subscriber;
    private bool _isSubscribing;

    public WebSocketHub(ILogger<WebSocketHub> logger, IConnectionMultiplexer redis)
    {
        _logger = logger;
        _redis = redis;
        _connections = new ConcurrentDictionary<string, ClientConnection>();
        _conversationSubscriptions = new ConcurrentDictionary<Guid, HashSet<string>>();
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
                userId,
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
                subscribers.Remove(connectionId);
                if (subscribers.Count == 0)
                {
                    _conversationSubscriptions.TryRemove(conversationId, out _);
                }
            }

            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Conexão encerrada",
                    CancellationToken.None);
            }

            webSocket.Dispose();
            _logger.LogInformation("WebSocket desconectado: ConnectionId={ConnectionId}", connectionId);
        }
    }

    private async Task HandleClientMessageAsync(ClientConnection connection, string message, CancellationToken ct)
    {
        try
        {
            var clientMessage = JsonSerializer.Deserialize<ClientMessage>(message);
            if (clientMessage == null) return;

            _logger.LogInformation("Mensagem recebida: Type={Type} ConnectionId={ConnectionId}",
                clientMessage.Type, connection.ConnectionId);

            switch (clientMessage.Type?.ToLower())
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
                    _logger.LogWarning("Tipo de mensagem desconhecido: {Type}", clientMessage.Type);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Erro ao deserializar mensagem do cliente");
            await SendToClientAsync(connection, new
            {
                type = "error",
                message = "Mensagem inválida"
            }, ct);
        }
    }

    private async Task HandleSubscribeAsync(ClientConnection connection, ClientMessage message, CancellationToken ct)
    {
        if (!Guid.TryParse(message.ConversationId, out var conversationId))
        {
            await SendToClientAsync(connection, new
            {
                type = "error",
                message = "ConversationId inválido"
            }, ct);
            return;
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
            conversationId = conversationId.ToString(),
            timestamp = DateTimeOffset.UtcNow
        }, ct);

        _logger.LogInformation("Cliente inscrito: ConnectionId={ConnectionId} ConversationId={ConversationId}",
            connection.ConnectionId, conversationId);
    }

    private async Task HandleUnsubscribeAsync(ClientConnection connection, ClientMessage message, CancellationToken ct)
    {
        if (!Guid.TryParse(message.ConversationId, out var conversationId))
        {
            return;
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
            conversationId = conversationId.ToString(),
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
                    // Extrair conversationId do canal "status:{guid}"
                    var parts = channelName.Split(':');
                    if (parts.Length != 2 || !Guid.TryParse(parts[1], out var conversationId))
                    {
                        return;
                    }

                    if (!_conversationSubscriptions.TryGetValue(conversationId, out var subscribers))
                    {
                        return; // Nenhum cliente inscrito nesta conversa
                    }

                    var notification = value.ToString();
                    _logger.LogInformation("Notificação Redis recebida: Channel={Channel}", channelName);

                    // Enviar para todos os clientes inscritos
                    var tasks = new List<Task>();
                    lock (subscribers)
                    {
                        foreach (var connectionId in subscribers)
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
                    _logger.LogError(ex, "Erro ao processar notificação Redis");
                }
            });

        _logger.LogInformation("Redis subscription iniciada para pattern: status:*");
    }

    private async Task SendToClientAsync(ClientConnection connection, object data, CancellationToken ct)
    {
        try
        {
            var json = data is string str ? str : JsonSerializer.Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);
            
            await connection.WebSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
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
            connection.WebSocket.Dispose();
        }
        _connections.Clear();
    }

    private record ClientConnection(
        string ConnectionId,
        WebSocket WebSocket,
        Guid UserId,
        Guid OrganizacaoId);

    private record ClientMessage(
        string? Type,
        string? ConversationId);
}
