using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

namespace Chat.Client.Wpf.Services;

public sealed class SocketChatClient : ISocketChatClient
{
    private Socket? _socket;
    private string? _username;
    private CancellationTokenSource? _cts;
    private System.Timers.Timer? _hb;

    private readonly Dictionary<string, FileRecvState> _recv = new();
    private readonly string _downloadsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChatDownloads");

    public event Action<string, string>? OnPrivate;
    public event Action<string, string, string>? OnGroup;
    public event Action<string[]>? OnUsers;
    public event Action<FileSavedArgs>? OnFileSaved;
    public event Action<long, long>? OnSendProgress;
    public event Action<string>? OnInfo;
    public event Action<string>? OnError;

    public SocketChatClient() => Directory.CreateDirectory(_downloadsDir);

    public async Task ConnectAsync(string host, int port, string username)
    {
        _username = username;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await _socket.ConnectAsync(host, port);

        await SendEnv(ProtocolUtil.Make(MessageType.Auth, null, null, new AuthRequest(username, null)));

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        _hb = new System.Timers.Timer(30_000);
        _hb.Elapsed += async (_, __) =>
        {
            var env = ProtocolUtil.Make(MessageType.Ping, null, null, new Ping());
            await SendEnv(env);
        };
        _hb.AutoReset = true;
        _hb.Start();
    }

    public Task SendPrivateTextAsync(string toUser, string text) =>
        SendEnv(ProtocolUtil.Make(MessageType.PrivateMsg, _username, toUser, new PrivateMessage(toUser, text)));

    public Task SendGroupTextAsync(string group, string text) =>
        SendEnv(ProtocolUtil.Make(MessageType.GroupMsg, _username, group, new GroupMessage(group, text)));

    public Task ListUsersAsync() =>
        SendEnv(ProtocolUtil.Make(MessageType.ListUsers, _username, null, new ListUsersRequest()));

    public Task CreateGroupAsync(string name) =>
        SendEnv(ProtocolUtil.Make(MessageType.CreateGroup, _username, null, new CreateGroupRequest(name)));

    public Task AddToGroupAsync(string name, string username) =>
        SendEnv(ProtocolUtil.Make(MessageType.AddToGroup, _username, null, new AddToGroupRequest(name, username)));

    public async Task SendFileAsync(string target, string path, int chunkSize = 64 * 1024)
    {
        if (_socket is null || _username is null) return;
        if (!File.Exists(path)) { OnInfo?.Invoke($"[File] não existe: {path}"); return; }

        var fi = new FileInfo(path);
        var id = Guid.NewGuid().ToString("N");
        var total = fi.Length;
        var count = (int)Math.Ceiling((double)total / chunkSize);

        OnInfo?.Invoke($"[File] Enviando '{fi.Name}' para {target} ({total:N0} bytes) ...");

        await SendEnv(ProtocolUtil.Make(MessageType.FileChunk, _username, target,
            new FileChunkHeader(id, target, fi.Name, total, chunkSize)));

        long sent = 0;
        int idx = 0;
        using var fs = fi.OpenRead();
        var buf = new byte[chunkSize];
        while (true)
        {
            int read = await fs.ReadAsync(buf, 0, buf.Length);
            if (read <= 0) break;
            var data = new byte[read];
            Buffer.BlockCopy(buf, 0, data, 0, read);
            await SendEnv(ProtocolUtil.Make(MessageType.FileChunk, _username, target,
                new FileChunk(id, idx++, count, data)));
            sent += read;
            OnSendProgress?.Invoke(sent, total);
        }
        OnInfo?.Invoke("[File] Envio finalizado.");
    }

    private async Task SendEnv(Envelope env)
    {
        if (_socket is null) return;
        var json = JsonSerializer.Serialize(env);
        await SocketFraming.SendFrameAsync(_socket, Encoding.UTF8.GetBytes(json));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        if (_socket is null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await SocketFraming.ReadFrameAsync(_socket, ct);
                if (frame is null) break;

                var env = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(frame));
                if (env is null) continue;

                switch (env.Type)
                {
                    case MessageType.PrivateMsg:
                        var pm = JsonMessageSerializer.Deserialize<PrivateMessage>(env.Payload);
                        if (pm is not null) OnPrivate?.Invoke(env.From ?? "?", pm.Text);
                        break;

                    case MessageType.GroupMsg:
                        var gm = JsonMessageSerializer.Deserialize<GroupMessage>(env.Payload);
                        if (gm is not null) OnGroup?.Invoke(gm.Group, env.From ?? "?", gm.Text);
                        break;

                    case MessageType.FileChunk:
                        await HandleIncomingFileAsync(env);
                        break;

                    case MessageType.Ack:
                        // pode ser lista de usuários também
                        try
                        {
                            var list = JsonMessageSerializer.Deserialize<ListUsersResponse>(env.Payload);
                            if (list?.Users is not null) { OnUsers?.Invoke(list.Users); break; }
                        }
                        catch { }
                        OnInfo?.Invoke("[Ack] " + env.Payload);
                        break;

                    case MessageType.Error:
                        var err = JsonMessageSerializer.Deserialize<ErrorMessage>(env.Payload);
                        OnError?.Invoke($"[Erro] {err?.Code}: {err?.Message}");
                        break;

                    default:
                        OnInfo?.Invoke($"[Info] {env.Type}: {env.Payload}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OnError?.Invoke("[Client] desconectado: " + ex.Message); }
    }

    private async Task HandleIncomingFileAsync(Envelope env)
    {
        try
        {
            var h = JsonMessageSerializer.Deserialize<FileChunkHeader>(env.Payload);
            if (h?.Id is not null && h.TotalBytes > 0)
            {
                var safe = string.Concat(h.FileName.Split(Path.GetInvalidFileNameChars()));
                var path = Path.Combine(_downloadsDir, $"recv_{h.Id}_{safe}");
                _recv[h.Id] = new FileRecvState(h.Id, env.From ?? "?", env.To ?? "?", path, h.TotalBytes);
                OnInfo?.Invoke($"[File] Recebendo '{h.FileName}' de {env.From} → {env.To} ({h.TotalBytes:N0} bytes)");
                return;
            }
        }
        catch { }

        try
        {
            var c = JsonMessageSerializer.Deserialize<FileChunk>(env.Payload);
            if (c?.Id is null) return;
            if (!_recv.TryGetValue(c.Id, out var st))
            {
                var path = Path.Combine(_downloadsDir, $"recv_{c.Id}_pending.bin");
                st = new FileRecvState(c.Id, env.From ?? "?", env.To ?? "?", path, 0);
                _recv[c.Id] = st;
            }
            await st.WriteAsync(c.Data);
            if (st.Total > 0 && st.Received >= st.Total)
            {
                await st.CloseAsync();
                OnFileSaved?.Invoke(new FileSavedArgs(st.From, st.Target, st.Path, Path.GetFileName(st.Path), st.Total));
                _recv.Remove(c.Id);
            }
        }
        catch { }
    }

    private sealed class FileRecvState
    {
        public string Id { get; }
        public string From { get; }
        public string Target { get; }
        public string Path { get; }
        public long Total { get; }
        public long Received { get; private set; }
        private readonly FileStream _fs;

        public FileRecvState(string id, string from, string target, string path, long total)
        {
            Id = id; From = from; Target = target; Path = path; Total = total;
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        }
        public async Task WriteAsync(byte[] data)
        {
            await _fs.WriteAsync(data, 0, data.Length);
            Received += data.Length;
        }
        public async Task CloseAsync() { await _fs.FlushAsync(); _fs.Close(); }
    }
}
