using Chat.Client.Wpf.Infrastructure;

namespace Chat.Client.Wpf.Models;

public sealed class MessageItem : ObservableObject
{
    public required string From { get; init; }
    public required string Text { get; init; }
    public required bool IsMine { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public bool IsFile { get; init; }
    public string? FilePath { get; init; }
    public string? FileName { get; init; }
    public long? FileSize { get; init; }
}
