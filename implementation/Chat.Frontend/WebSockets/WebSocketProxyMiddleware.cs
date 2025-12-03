using System.Net.WebSockets;
using System.Text;

namespace Chat.Frontend.WebSockets;

/// <summary>
/// Middleware que faz proxy de conexões WebSocket do frontend para o Chat.Api
/// </summary>
public class WebSocketProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<WebSocketProxyMiddleware> _logger;
    private readonly string _chatApiWebSocketUrl;

    public WebSocketProxyMiddleware(
        RequestDelegate next,
        ILogger<WebSocketProxyMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;

        // URL do WebSocket do Chat.Api
        var chatApiUrl = configuration["ChatApi:BaseUrl"] ??
                        Environment.GetEnvironmentVariable("CHAT_API_URL") ??
                        "http://localhost:5000";

        // Converter http:// para ws:// e https:// para wss://
        _chatApiWebSocketUrl = chatApiUrl
            .Replace("http://", "ws://")
            .Replace("https://", "wss://");

        _logger.LogInformation("WebSocket proxy configured: Frontend -> {BackendUrl}", _chatApiWebSocketUrl);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Verificar se é requisição WebSocket para /ws/status
        if (!context.Request.Path.StartsWithSegments("/ws/status"))
        {
            await _next(context);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected WebSocket request");
            return;
        }

        // Extrair token JWT da query string ou header
        var token = context.Request.Query["access_token"].FirstOrDefault() ??
                   context.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");

        if (string.IsNullOrEmpty(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing access token");
            return;
        }

        _logger.LogInformation("WebSocket proxy: New connection from {RemoteIp}", 
            context.Connection.RemoteIpAddress);

        // Aceitar conexão do cliente
        using var clientWebSocket = await context.WebSockets.AcceptWebSocketAsync();

        // Conectar ao Chat.Api backend
        using var backendWebSocket = new ClientWebSocket();
        
        try
        {
            // Configurar backend connection
            var backendUri = new Uri($"{_chatApiWebSocketUrl}/ws/status?access_token={token}");
            
            _logger.LogInformation("WebSocket proxy: Connecting to backend {BackendUri}", 
                backendUri.ToString().Replace(token, "***"));

            await backendWebSocket.ConnectAsync(backendUri, context.RequestAborted);

            _logger.LogInformation("WebSocket proxy: Connected to backend successfully");

            // Criar tasks para bidirectional proxy
            var clientToBackend = ProxyMessagesAsync(
                clientWebSocket, 
                backendWebSocket, 
                "Client->Backend", 
                context.RequestAborted);

            var backendToClient = ProxyMessagesAsync(
                backendWebSocket, 
                clientWebSocket, 
                "Backend->Client", 
                context.RequestAborted);

            // Aguardar qualquer um terminar
            await Task.WhenAny(clientToBackend, backendToClient);

            _logger.LogInformation("WebSocket proxy: Connection closed");
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "WebSocket proxy error: {Message}", ex.Message);
            
            if (clientWebSocket.State == WebSocketState.Open)
            {
                await clientWebSocket.CloseAsync(
                    WebSocketCloseStatus.InternalServerError,
                    "Backend connection failed",
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in WebSocket proxy");
        }
    }

    private async Task ProxyMessagesAsync(
        WebSocket source,
        WebSocket destination,
        string direction,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 4];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Receber do source
                var result = await source.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("WebSocket proxy [{Direction}]: Close received", direction);
                    
                    if (destination.State == WebSocketState.Open)
                    {
                        await destination.CloseAsync(
                            result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            result.CloseStatusDescription,
                            cancellationToken);
                    }
                    break;
                }

                // Log da mensagem (apenas para debug)
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogDebug("WebSocket proxy [{Direction}]: {Message}", 
                        direction, 
                        message.Length > 100 ? message.Substring(0, 100) + "..." : message);
                }

                // Encaminhar para destination
                if (destination.State == WebSocketState.Open)
                {
                    await destination.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType,
                        result.EndOfMessage,
                        cancellationToken);
                }
                else
                {
                    _logger.LogWarning("WebSocket proxy [{Direction}]: Destination is not open, closing source", 
                        direction);
                    break;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogInformation("WebSocket proxy [{Direction}]: Connection closed prematurely", direction);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WebSocket proxy [{Direction}]: Operation cancelled", direction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket proxy [{Direction}]: Error - {Message}", 
                direction, ex.Message);
            throw;
        }
    }
}

/// <summary>
/// Extension method para registrar o middleware
/// </summary>
public static class WebSocketProxyMiddlewareExtensions
{
    public static IApplicationBuilder UseWebSocketProxy(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<WebSocketProxyMiddleware>();
    }
}
