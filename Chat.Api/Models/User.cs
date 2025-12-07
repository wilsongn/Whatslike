// Chat.Api/Models/User.cs
namespace Chat.Api.Models;

public class User
{
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class UserSearchResult
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
}

public class UserPresence
{
    public Guid UserId { get; set; }
    public string Status { get; set; } = "offline"; // online, offline, away, busy
    public DateTimeOffset LastSeen { get; set; }
    public string? ConnectionId { get; set; }
    public string? DeviceType { get; set; }
    public string? DeviceInfo { get; set; }
}
