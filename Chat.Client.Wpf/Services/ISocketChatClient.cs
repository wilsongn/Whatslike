namespace Chat.Client.Wpf.Services;

public interface ISocketChatClient
{
    Task ConnectAsync(string host, int port, string username);
    Task SendPrivateTextAsync(string toUser, string text);
    Task SendGroupTextAsync(string group, string text);
    Task SendFileAsync(string target, string path, int chunkSize = 64 * 1024);
    Task ListUsersAsync();
    Task CreateGroupAsync(string name);
    Task AddToGroupAsync(string name, string username);

    event Action<string /*from*/, string /*text*/>? OnPrivate;
    event Action<string /*group*/, string /*from*/, string /*text*/>? OnGroup;
    event Action<string[] /*users*/>? OnUsers;
    event Action<FileSavedArgs>? OnFileSaved;
    event Action<long /*sent*/, long /*total*/>? OnSendProgress;
    event Action<string>? OnInfo;
    event Action<string>? OnError;
}

public sealed record FileSavedArgs(string From, string Target, string Path, string FileName, long TotalBytes);
