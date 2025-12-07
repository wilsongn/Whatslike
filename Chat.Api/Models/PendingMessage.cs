// Chat.Api/Models/PendingMessage.cs
namespace Chat.Api.Models;

public class PendingMessage
{
    public Guid UserId { get; set; }           // Destinatário offline
    public Guid MessageId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string? SenderName { get; set; }
    public string? Content { get; set; }
    public string ContentType { get; set; } = "text"; // text, file, image
    public string? FileId { get; set; }
    public string? FileName { get; set; }
    public string? FileExtension { get; set; }
    public long? FileSize { get; set; }
    public string? FileOrganizationId { get; set; }
    public string? Channel { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
