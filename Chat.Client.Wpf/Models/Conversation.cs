using System.Collections.ObjectModel;
using System.Windows;
using Chat.Client.Wpf.Infrastructure;

namespace Chat.Client.Wpf.Models;

public sealed class Conversation : ObservableObject
{
    public required ConversationType Type { get; init; }
    public required string Id { get; init; }          // username OU group name
    public required string DisplayName { get; init; }

    public ObservableCollection<MessageItem> Messages { get; } = new();

    private int _unread;
    public int Unread { get => _unread; set => SetProperty(ref _unread, value); }

    private string _lastPreview = string.Empty;
    public string LastPreview { get => _lastPreview; set => SetProperty(ref _lastPreview, value); }

    public void Add(MessageItem msg, bool isActive)
    {
        var d = Application.Current?.Dispatcher;
        if (d is not null && !d.CheckAccess())
        {
            d.Invoke(() => Add(msg, isActive));
            return;
        }

        Messages.Add(msg);
        LastPreview = msg.IsFile ? $"📎 {msg.FileName}" : msg.Text;
        if (!isActive) Unread++;
    }
}
