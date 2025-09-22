using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

namespace Chat.Tests.Integration;

public sealed class TestChatClient : IAsyncDisposable
{
    private readonly Socket _s;
    private readonly CancellationTokenSource _cts = new();
    public string Username { get; }
    public List<string> Inbox = new();
    public List<string> GroupsInbox = new();
    public List<string> Files = new();

    public TestChatClient(string username)
    {
        Username = username;
        _s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }

    public async Task ConnectAsync(string host, int port)
    {
        await _s.ConnectAsync(IPAddress.Parse(host), port);
        await SendAsync(ProtocolUtil.Make(MessageType.Auth, null, null, new AuthRequest(Username, null)));
        _ = Task.Run(ReceiveLoop);
    }

    public Task SendPrivateAsync(string to, string text) =>
        SendAsync(ProtocolUtil.Make(MessageType.PrivateMsg, Username, to, new PrivateMessage(to, text)));

    public Task CreateGroupAsync(string name) =>
        SendAsync(ProtocolUtil.Make(MessageType.CreateGroup, Username, null, new CreateGroupRequest(name)));

    public Task AddToGroupAsync(string name, string user) =>
        SendAsync(ProtocolUtil.Make(MessageType.AddToGroup, Username, null, new AddToGroupRequest(name, user)));

    public Task SendGroupAsync(string group, string text) =>
        SendAsync(ProtocolUtil.Make(MessageType.GroupMsg, Username, group, new GroupMessage(group, text)));

    public async Task SendFileAsync(string target, byte[] content, string fileName, int chunkSize = 32 * 1024)
    {
        var id = Guid.NewGuid().ToString("N");
        await SendAsync(ProtocolUtil.Make(MessageType.FileChunk, Username, target,
            new FileChunkHeader(id, target, fileName, content.LongLength, chunkSize)));

        int idx = 0;
        for (int offset = 0; offset < content.Length; offset += chunkSize)
        {
            var slice = content.AsSpan(offset, Math.Min(chunkSize, content.Length - offset)).ToArray();
            await SendAsync(ProtocolUtil.Make(MessageType.FileChunk, Username, target,
                new FileChunk(id, idx++, (int)Math.Ceiling((double)content.Length / chunkSize), slice)));
        }
    }

    private async Task SendAsync(Envelope env)
    {
        var json = JsonSerializer.Serialize(env);
        await SocketFraming.SendFrameAsync(_s, Encoding.UTF8.GetBytes(json));
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await SocketFraming.ReadFrameAsync(_s, _cts.Token);
                if (frame is null) break;
                var env = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(frame));
                if (env is null) continue;

                switch (env.Type)
                {
                    case MessageType.PrivateMsg:
                        var pm = JsonMessageSerializer.Deserialize<PrivateMessage>(env.Payload)!;
                        Inbox.Add($"{env.From}:{pm.Text}");
                        break;
                    case MessageType.GroupMsg:
                        var gm = JsonMessageSerializer.Deserialize<GroupMessage>(env.Payload)!;
                        GroupsInbox.Add($"{gm.Group}:{env.From}:{gm.Text}");
                        break;
                    case MessageType.FileChunk:
                        // Apenas marca que chegou header/arquivo
                        try
                        {
                            var h = JsonMessageSerializer.Deserialize<FileChunkHeader>(env.Payload);
                            if (h != null) Files.Add($"header:{h.FileName}:{h.TotalBytes}");
                        }
                        catch { /* ignore */ }
                        break;
                }
            }
        }
        catch { /* ignore on dispose */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); _s.Shutdown(SocketShutdown.Both); } catch { }
        await Task.Delay(50);
        _s.Dispose();
        _cts.Dispose();
    }
}
