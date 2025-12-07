// Chat.Api/Controllers/PendingMessagesController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Chat.Api.Services;
using System.Security.Claims;

namespace Chat.Api.Controllers;

[ApiController]
[Route("api/v1/messages/pending")]
[Authorize]
public class PendingMessagesController : ControllerBase
{
    private readonly IOfflineMessageService _offlineMessageService;
    private readonly ILogger<PendingMessagesController> _logger;

    public PendingMessagesController(
        IOfflineMessageService offlineMessageService,
        ILogger<PendingMessagesController> logger)
    {
        _offlineMessageService = offlineMessageService;
        _logger = logger;
    }

    /// <summary>
    /// Obtém todas as mensagens pendentes do usuário
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPendingMessages([FromQuery] int limit = 100)
    {
        var userId = GetUserIdFromToken();

        var messages = await _offlineMessageService.GetPendingMessagesAsync(userId);
        
        return Ok(new
        {
            userId,
            count = messages.Count(),
            messages = messages.Select(m => new
            {
                messageId = m.MessageId,
                conversationId = m.ConversationId,
                senderId = m.SenderId,
                senderName = m.SenderName,
                content = m.Content,
                contentType = m.ContentType,
                fileId = m.FileId,
                fileName = m.FileName,
                fileExtension = m.FileExtension,
                fileSize = m.FileSize,
                fileOrganizationId = m.FileOrganizationId,
                channel = m.Channel,
                createdAt = m.CreatedAt
            })
        });
    }

    /// <summary>
    /// Obtém contagem de mensagens pendentes
    /// </summary>
    [HttpGet("count")]
    public async Task<IActionResult> GetPendingCount()
    {
        var userId = GetUserIdFromToken();

        var count = await _offlineMessageService.GetPendingCountAsync(userId);
        
        return Ok(new
        {
            userId,
            count
        });
    }

    /// <summary>
    /// Confirma recebimento de mensagens (remove das pendentes)
    /// </summary>
    [HttpPost("ack")]
    public async Task<IActionResult> AcknowledgeMessages([FromBody] AckMessagesRequest request)
    {
        var userId = GetUserIdFromToken();

        if (request.MessageIds == null || request.MessageIds.Count == 0)
        {
            return BadRequest(new { error = "messageIds is required" });
        }

        await _offlineMessageService.AcknowledgeMessagesAsync(userId, request.MessageIds);

        return Ok(new
        {
            success = true,
            acknowledged = request.MessageIds.Count
        });
    }

    /// <summary>
    /// Solicita entrega de mensagens pendentes via WebSocket
    /// </summary>
    [HttpPost("deliver")]
    public async Task<IActionResult> DeliverPendingMessages()
    {
        var userId = GetUserIdFromToken();

        await _offlineMessageService.DeliverPendingMessagesAsync(userId);

        return Ok(new
        {
            success = true,
            message = "Pending messages are being delivered via WebSocket"
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

public class AckMessagesRequest
{
    public List<Guid> MessageIds { get; set; } = new();
}
