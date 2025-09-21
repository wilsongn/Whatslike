using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

namespace Chat.Client.Wpf;

public sealed class SocketClient
{
    private Socket? _socket;
    private string? _username;
    private CancellationTokenSource? _cts;

    // Recepção de arquivos
    private readonly Dictionary<string, FileRecvState> _recv = new();
    private readonly string _downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChatDownloads");

    public event Action<string>? OnInfo;
    public event Action<string>? OnMessage;
    public event Action<string>? OnError;
    public event Action<string[]>? OnUsers;
    public event Action<long, long>? OnProgress; // sent,total (envio)
    public event Action<string>? OnFileSaved;

    public SocketClient() => Directory.CreateDirectory(_downloadsDir);

    public async Task ConnectAsync(string host, int port, string user)
    {
        _username = user;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await _socket.ConnectAsync(host, port);

        // AUTH
        await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.Auth, null, null, new AuthRequest(user, null)));

        // Receiver
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public async Task SendPrivateTextAsync(string toUser, string text)
    {
        await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.PrivateMsg, _username, toUser, new PrivateMessage(toUser, text)));
        OnInfo?.Invoke($"[Você → {toUser}] {text}");
    }

    public async Task SendGroupTextAsync(string group, string text)
    {
        await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.GroupMsg, _username, group, new GroupMessage(group, text)));
        OnInfo?.Invoke($"[Você → G:{group}] {text}");
    }

    public async Task ListUsersAsync() =>
        await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.ListUsers, _username, null, new ListUsersRequest()));

    public async Task CreateGroupAsync(string name) =>
        await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.CreateGroup, _username, null, new CreateGroupRequest(name)));

    public async Task AddToGroupAsync(string name, string username) =>
        await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.AddToGroup, _username, null, new AddToGroupRequest(name, username)));

    public async Task SendFileAsync(string target, string path, int chunkSize = 64 * 1024)
    {
        if (_socket is null || string.IsNullOrWhiteSpace(_username)) return;
        if (!File.Exists(path)) { OnInfo?.Invoke($"[File] não existe: {path}"); return; }

        var fi = new FileInfo(path);
        var id = Guid.NewGuid().ToString("N");
        var total = fi.Length;
        var count = (int)Math.Ceiling((double)total / chunkSize);

        OnInfo?.Invoke($"[File] Enviando para '{target}': {fi.Name} ({total:N0} bytes) em {count} chunk(s) ...");

        // Header
        var header = new FileChunkHeader(id, target, fi.Name, total, chunkSize);
        await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.FileChunk, _username, target, header));

        // Chunks
        long sentBytes = 0;
        int index = 0;
        using var fs = fi.OpenRead();
        var buffer = new byte[chunkSize];

        while (true)
        {
            int read = await fs.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0) break;

            var data = new byte[read];
            Buffer.BlockCopy(buffer, 0, data, 0, read);

            var chunk = new FileChunk(id, index, count, data);
            await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.FileChunk, _username, target, chunk));

            sentBytes += read;
            index++;
            OnProgress?.Invoke(sentBytes, total);
        }

        OnInfo?.Invoke("[File] Envio finalizado.");
    }

    private async Task SendEnvelopeAsync(Envelope env)
    {
        if (_socket is null) return;
        var json = JsonSerializer.Serialize(env);
        var bytes = Encoding.UTF8.GetBytes(json);
        await SocketFraming.SendFrameAsync(_socket, bytes);
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
                        OnMessage?.Invoke($"[PM] {env.From} → você: {pm?.Text}");
                        break;

                    case MessageType.GroupMsg:
                        var gm = JsonMessageSerializer.Deserialize<GroupMessage>(env.Payload);
                        OnMessage?.Invoke($"[G:{gm?.Group}] {env.From}: {gm?.Text}");
                        break;

                    case MessageType.FileChunk:
                        await HandleIncomingFileAsync(env);
                        break;

                    case MessageType.Ack:
                        // pode ser AckMessage ou ListUsersResponse
                        var listed = TryUsers(env.Payload);
                        if (!listed)
                        {
                            var ack = JsonMessageSerializer.Deserialize<AckMessage>(env.Payload);
                            OnInfo?.Invoke($"[Ack] {ack?.CorrelationId} {ack?.Note}");
                        }
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
        catch (Exception ex)
        {
            OnError?.Invoke("[Client] desconectado: " + ex.Message);
        }
    }

    private bool TryUsers(string payload)
    {
        try
        {
            var list = JsonMessageSerializer.Deserialize<ListUsersResponse>(payload);
            if (list?.Users is not null)
            {
                OnUsers?.Invoke(list.Users);
                return true;
            }
        }
        catch { }
        return false;
    }

    private async Task HandleIncomingFileAsync(Envelope env)
    {
        // Header?
        try
        {
            var h = JsonMessageSerializer.Deserialize<FileChunkHeader>(env.Payload);
            if (h?.Id is not null && h.TotalBytes > 0)
            {
                var safeName = string.Concat(h.FileName.Split(Path.GetInvalidFileNameChars()));
                var savePath = Path.Combine(_downloadsDir, $"recv_{h.Id}_{safeName}");
                var st = new FileRecvState(h.Id, savePath, h.TotalBytes, h.ChunkSize);
                _recv[h.Id] = st;
                OnInfo?.Invoke($"[File] Recebendo de {env.From}: '{h.FileName}' ({h.TotalBytes:N0} bytes)");
                return;
            }
        }
        catch { }

        // Chunk?
        try
        {
            var c = JsonMessageSerializer.Deserialize<FileChunk>(env.Payload);
            if (c?.Id is not null)
            {
                if (!_recv.TryGetValue(c.Id, out var st))
                {
                    var savePath = Path.Combine(_downloadsDir, $"recv_{c.Id}_pending.bin");
                    st = new FileRecvState(c.Id, savePath, 0, 0);
                    _recv[c.Id] = st;
                }

                await st.WriteAsync(c);

                if (st.TotalBytes > 0 && st.Received >= st.TotalBytes)
                {
                    await st.CloseAsync();
                    OnFileSaved?.Invoke(st.SavePath);
                    _recv.Remove(c.Id);
                }
                return;
            }
        }
        catch { }

        OnInfo?.Invoke("[File] payload desconhecido.");
    }

    private sealed class FileRecvState
    {
        public string Id { get; }
        public string SavePath { get; }
        public long TotalBytes { get; }
        public int ChunkSize { get; }
        public long Received { get; private set; }
        private readonly FileStream _fs;

        public FileRecvState(string id, string savePath, long total, int chunk)
        {
            Id = id;
            SavePath = savePath;
            TotalBytes = total;
            ChunkSize = chunk;
            _fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        }

        public async Task WriteAsync(FileChunk c)
        {
            await _fs.WriteAsync(c.Data, 0, c.Data.Length);
            Received += c.Data.Length;
        }

        public async Task CloseAsync()
        {
            await _fs.FlushAsync();
            _fs.Close();
        }
    }
}
