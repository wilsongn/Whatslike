// Chat.Api/Controllers/PresenceController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Chat.Api.Models;
using Chat.Api.Repositories;
using System.Security.Claims;

namespace Chat.Api.Controllers;

[ApiController]
[Route("api/v1/presence")]
[Authorize]
public class PresenceController : ControllerBase
{
    private readonly IPresenceRepository _presenceRepository;
    private readonly ILogger<PresenceController> _logger;

    public PresenceController(
        IPresenceRepository presenceRepository,
        ILogger<PresenceController> logger)
    {
        _presenceRepository = presenceRepository;
        _logger = logger;
    }

    /// <summary>
    /// Obtém o status de presença do usuário logado
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetMyPresence()
    {
        var userId = GetUserIdFromToken();

        var presence = await _presenceRepository.GetPresenceAsync(userId);
        
        if (presence == null)
        {
            return Ok(new
            {
                userId,
                status = "offline",
                lastSeen = (DateTimeOffset?)null
            });
        }

        return Ok(new
        {
            userId = presence.UserId,
            status = presence.Status,
            lastSeen = presence.LastSeen,
            deviceType = presence.DeviceType
        });
    }

    /// <summary>
    /// Obtém o status de presença de um usuário específico
    /// </summary>
    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> GetUserPresence(Guid userId)
    {
        var presence = await _presenceRepository.GetPresenceAsync(userId);
        
        if (presence == null)
        {
            return Ok(new
            {
                userId,
                status = "offline",
                lastSeen = (DateTimeOffset?)null,
                isOnline = false
            });
        }

        var isOnline = await _presenceRepository.IsOnlineAsync(userId);

        return Ok(new
        {
            userId = presence.UserId,
            status = presence.Status,
            lastSeen = presence.LastSeen,
            isOnline
        });
    }

    /// <summary>
    /// Obtém o status de presença de múltiplos usuários
    /// </summary>
    [HttpPost("batch")]
    public async Task<IActionResult> GetBatchPresence([FromBody] BatchPresenceRequest request)
    {
        if (request.UserIds == null || request.UserIds.Count == 0)
        {
            return BadRequest(new { error = "userIds is required" });
        }

        if (request.UserIds.Count > 100)
        {
            return BadRequest(new { error = "Maximum 100 users per request" });
        }

        var presences = await _presenceRepository.GetPresenceBatchAsync(request.UserIds);
        
        var result = new List<object>();
        foreach (var userId in request.UserIds)
        {
            if (presences.TryGetValue(userId, out var presence))
            {
                var isOnline = presence.Status == "online" && 
                              presence.LastSeen > DateTimeOffset.UtcNow.AddMinutes(-5);
                
                result.Add(new
                {
                    userId = presence.UserId,
                    status = presence.Status,
                    lastSeen = presence.LastSeen,
                    isOnline
                });
            }
            else
            {
                result.Add(new
                {
                    userId,
                    status = "offline",
                    lastSeen = (DateTimeOffset?)null,
                    isOnline = false
                });
            }
        }

        return Ok(new { presences = result });
    }

    /// <summary>
    /// Atualiza o status do usuário (away, busy, etc)
    /// </summary>
    [HttpPut("status")]
    public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusRequest request)
    {
        var userId = GetUserIdFromToken();

        var validStatuses = new[] { "online", "away", "busy" };
        if (!validStatuses.Contains(request.Status?.ToLowerInvariant()))
        {
            return BadRequest(new { error = "Invalid status. Use: online, away, busy" });
        }

        await _presenceRepository.UpdateStatusAsync(userId, request.Status!);

        return Ok(new
        {
            userId,
            status = request.Status!.ToLowerInvariant()
        });
    }

    /// <summary>
    /// Heartbeat para manter a sessão ativa
    /// </summary>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat()
    {
        var userId = GetUserIdFromToken();

        await _presenceRepository.UpdateLastSeenAsync(userId);

        return Ok(new { success = true, timestamp = DateTimeOffset.UtcNow });
    }

    /// <summary>
    /// Marca o usuário como online (usado pelo WebSocket ao conectar)
    /// </summary>
    [HttpPost("online")]
    public async Task<IActionResult> SetOnline([FromBody] SetOnlineRequest? request = null)
    {
        var userId = GetUserIdFromToken();
        var connectionId = request?.ConnectionId ?? Guid.NewGuid().ToString();
        var deviceType = request?.DeviceType ?? "web";
        var deviceInfo = request?.DeviceInfo;

        await _presenceRepository.SetOnlineAsync(userId, connectionId, deviceType, deviceInfo);

        return Ok(new
        {
            userId,
            status = "online",
            connectionId
        });
    }

    /// <summary>
    /// Marca o usuário como offline (usado pelo WebSocket ao desconectar)
    /// </summary>
    [HttpPost("offline")]
    public async Task<IActionResult> SetOffline()
    {
        var userId = GetUserIdFromToken();

        await _presenceRepository.SetOfflineAsync(userId);

        return Ok(new
        {
            userId,
            status = "offline"
        });
    }

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
}

// ============================================
// Request DTOs
// ============================================

public class BatchPresenceRequest
{
    public List<Guid> UserIds { get; set; } = new();
}

public class UpdateStatusRequest
{
    public string? Status { get; set; }
}

public class SetOnlineRequest
{
    public string? ConnectionId { get; set; }
    public string? DeviceType { get; set; }
    public string? DeviceInfo { get; set; }
}
