// Chat.Api/Models/Conversation.cs
namespace Chat.Api.Models;

public class Conversation
{
    public Guid ConversationId { get; set; }
    public Guid OrganizationId { get; set; }
    public string Type { get; set; } = "private"; // 'private' ou 'group'
    public string? Name { get; set; } // Nome do grupo (null para private)
    public string? Description { get; set; }
    public string? AvatarUrl { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Metadata { get; set; } // JSON com configs extras
}

public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "member"; // 'owner', 'admin', 'member'
    public DateTimeOffset JoinedAt { get; set; }
    public Guid? AddedBy { get; set; }
    public string? Nickname { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
}

public class UserConversation
{
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public string Type { get; set; } = "private";
    public string? Name { get; set; } // Nome do grupo ou displayName do outro usuário
    public string? AvatarUrl { get; set; }
    public string? LastMessagePreview { get; set; }
    public string? LastMessageSender { get; set; }
    public DateTimeOffset LastMessageAt { get; set; }
    public int UnreadCount { get; set; }
    public bool IsMuted { get; set; }
    public bool IsPinned { get; set; }
}

public class ConversationDetails
{
    public Guid ConversationId { get; set; }
    public string Type { get; set; } = "private";
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? AvatarUrl { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<ConversationMemberInfo> Members { get; set; } = new();
}

public class ConversationMemberInfo
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = "member";
    public DateTimeOffset JoinedAt { get; set; }
}
