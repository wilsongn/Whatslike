using System.Net.WebSockets;
using System.Security.Claims;

namespace Chat.Api.WebSockets;

/// <summary>
/// Middleware para gerenciar conexões WebSocket
/// </summary>
public class WebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly WebSocketHub _hub;
    private readonly ILogger<WebSocketMiddleware> _logger;

    public WebSocketMiddleware(
        RequestDelegate next,
        WebSocketHub hub,
        ILogger<WebSocketMiddleware> logger)
    {
        _next = next;
        _hub = hub;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Verificar se é uma requisição WebSocket para o path correto
        if (!context.Request.Path.StartsWithSegments("/ws/status"))
        {
            await _next(context);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Esperado WebSocket request");
            return;
        }

        // Extrair informações do usuário autenticado
        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var tenantIdClaim = context.User.FindFirst("tenant_id")?.Value;

        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("User ID inválido ou ausente");
            return;
        }

        Guid? organizacaoId = null;
        if (Guid.TryParse(tenantIdClaim, out var orgId))
        {
            organizacaoId = orgId;
        }

        _logger.LogInformation("Nova conexão WebSocket: UserId={UserId} OrganizacaoId={OrganizacaoId}",
            userId, organizacaoId);

        // Aceitar WebSocket
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        
        // Delegar para o Hub
        await _hub.HandleConnectionAsync(
            webSocket,
            userId,
            organizacaoId,
            context.RequestAborted);
    }
}

/// <summary>
/// Extension method para registrar o middleware
/// </summary>
public static class WebSocketMiddlewareExtensions
{
    public static IApplicationBuilder UseWebSocketHub(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<WebSocketMiddleware>();
    }
}
