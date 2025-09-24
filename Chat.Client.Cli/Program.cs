using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

namespace Chat.Client.Cli;

internal static class Program
{
    private static Socket? _socket;
    private static string? _username;
    private static CancellationTokenSource? _cts;

    // Estado de recebimento de arquivos
    private static readonly Dictionary<string, FileRecvState> _recv = new();

    private const int DefaultChunk = 64 * 1024; // 64 KiB
    private static readonly string DownloadsDir = Path.Combine(Environment.CurrentDirectory, "Downloads");

    public static async Task Main(string[] args)
    {
        string host = "127.0.0.1";
        int port = 5000;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host": host = args[++i]; break;
                case "--port": port = int.Parse(args[++i]); break;
                case "--user": _username = args[++i]; break;
            }
        }

        Directory.CreateDirectory(DownloadsDir);

        Console.WriteLine($"[Client] Conectando em {host}:{port} ...");
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await _socket.ConnectAsync(host, port);

        while (string.IsNullOrWhiteSpace(_username))
        {
            Console.Write("Username: ");
            _username = Console.ReadLine();
        }

        // AUTH
        await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.Auth, null, null, new AuthRequest(_username!, null)));

        PrintHelp();

        _cts = new CancellationTokenSource();
        var recvTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        // Loop de comandos
        while (true)
        {
            var line = Console.ReadLine();
            if (line is null) continue;
            if (line.Equals("/quit", StringComparison.OrdinalIgnoreCase)) break;
            if (line.Equals("/help", StringComparison.OrdinalIgnoreCase)) { PrintHelp(); continue; }

            // Mensagens
            if (line.StartsWith("/msg "))
            {
                var parts = SplitN(line, 3);
                if (parts.Length < 3) { Console.WriteLine("uso: /msg <user> <texto>"); continue; }
                await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.PrivateMsg, _username, parts[1],
                    new PrivateMessage(parts[1], parts[2])));
                continue;
            }
            if (line.Equals("/users", StringComparison.OrdinalIgnoreCase))
            {
                await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.ListUsers, _username, null, new ListUsersRequest()));
                continue;
            }
            if (line.StartsWith("/group create "))
            {
                var name = line.Substring("/group create ".Length).Trim();
                if (string.IsNullOrWhiteSpace(name)) { Console.WriteLine("uso: /group create <nome>"); continue; }
                await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.CreateGroup, _username, null, new CreateGroupRequest(name)));
                continue;
            }
            if (line.StartsWith("/group add "))
            {
                var parts = SplitN(line, 4);
                if (parts.Length < 4) { Console.WriteLine("uso: /group add <grupo> <user>"); continue; }
                await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.AddToGroup, _username, null, new AddToGroupRequest(parts[2], parts[3])));
                continue;
            }
            if (line.StartsWith("/gmsg "))
            {
                var parts = SplitN(line, 3);
                if (parts.Length < 3) { Console.WriteLine("uso: /gmsg <grupo> <texto>"); continue; }
                await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.GroupMsg, _username, parts[1], new GroupMessage(parts[1], parts[2])));
                continue;
            }

            // ===== Envio de arquivos =====
            // /sendfile <user> <caminho>
            if (line.StartsWith("/sendfile "))
            {
                var parts = SplitN(line, 3);
                if (parts.Length < 3) { Console.WriteLine("uso: /sendfile <user> <caminho>"); continue; }
                var user = parts[1];
                var path = parts[2].Trim('"');
                await SendFileAsync(user, path);
                continue;
            }
            // /gfile <grupo> <caminho>
            if (line.StartsWith("/gfile "))
            {
                var parts = SplitN(line, 3);
                if (parts.Length < 3) { Console.WriteLine("uso: /gfile <grupo> <caminho>"); continue; }
                var group = parts[1];
                var path = parts[2].Trim('"');
                await SendFileAsync(group, path);
                continue;
            }

            Console.WriteLine("comando não reconhecido. Use /help");
        }

        _cts.Cancel();
        try { await recvTask; } catch { }
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { }
        _socket?.Dispose();
    }

    // ========= ENVIO =========

    private static async Task SendFileAsync(string target, string path, int chunkSize = DefaultChunk)
    {
        if (_socket is null) return;
        if (!File.Exists(path)) { Console.WriteLine($"[File] não existe: {path}"); return; }

        var fi = new FileInfo(path);
        var id = Guid.NewGuid().ToString("N");
        var total = fi.Length;
        var count = (int)Math.Ceiling((double)total / chunkSize);

        Console.WriteLine($"[File] Enviando para '{target}': {fi.Name} ({total:N0} bytes) em {count} chunk(s) ...");

        // 1) Header
        var header = new FileChunkHeader(id, target, fi.Name, total, chunkSize);
        await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.FileChunk, _username, target, header));

        // 2) Chunks
        var sentBytes = 0L;
        int index = 0;
        using var fs = fi.OpenRead();
        var buffer = new byte[chunkSize];

        while (true)
        {
            int read = await fs.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0) break;

            var chunkData = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunkData, 0, read);

            var chunk = new FileChunk(id, index, count, chunkData);
            await SendEnvelopeAsync(ProtocolUtil.Make(MessageType.FileChunk, _username, target, chunk));

            sentBytes += read;
            index++;

            // progresso
            var pct = (double)sentBytes / total * 100.0;
            Console.Write($"\r[File] {sentBytes:N0}/{total:N0} bytes ({pct:0.0}%)");
        }

        Console.WriteLine();
        Console.WriteLine("[File] Envio finalizado.");
    }

    private static async Task SendEnvelopeAsync(Envelope env)
    {
        if (_socket is null) return;
        var json = JsonSerializer.Serialize(env);
        var bytes = Encoding.UTF8.GetBytes(json);
        await SocketFraming.SendFrameAsync(_socket, bytes);
    }


    private static async Task ReceiveLoopAsync(CancellationToken ct)
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
                        Console.WriteLine($"[PM] {env.From} → você: {pm?.Text}");
                        break;

                    case MessageType.GroupMsg:
                        var gm = JsonMessageSerializer.Deserialize<GroupMessage>(env.Payload);
                        Console.WriteLine($"[G:{gm?.Group}] {env.From}: {gm?.Text}");
                        break;

                    case MessageType.FileChunk:
                        await HandleIncomingFileAsync(env);
                        break;

                    case MessageType.Ack:
                        var printed = false;
                        try
                        {
                            var list = JsonMessageSerializer.Deserialize<ListUsersResponse>(env.Payload);
                            if (list?.Users is not null) { Console.WriteLine("[Users] " + string.Join(", ", list.Users)); printed = true; }
                        }
                        catch { }
                        if (!printed)
                        {
                            try
                            {
                                var ack = JsonMessageSerializer.Deserialize<AckMessage>(env.Payload);
                                if (ack is not null) { Console.WriteLine($"[Ack] {ack.CorrelationId} {ack.Note}"); printed = true; }
                            }
                            catch { }
                        }
                        if (!printed) Console.WriteLine("[Ack] " + env.Payload);
                        break;

                    case MessageType.Error:
                        var err = JsonMessageSerializer.Deserialize<ErrorMessage>(env.Payload);
                        Console.WriteLine($"[Erro] {err?.Code}: {err?.Message}");
                        break;

                    default:
                        Console.WriteLine($"[Info] {env.Type}: {env.Payload}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine("[Client] desconectado: " + ex.Message);
        }
    }

    private static async Task HandleIncomingFileAsync(Envelope env)
    {
        // Pode ser Header OU Chunk; detecta por deserialização
        try
        {
            var header = JsonMessageSerializer.Deserialize<FileChunkHeader>(env.Payload);
            if (header?.Id is not null && header.TotalBytes > 0 && header.ChunkSize > 0)
            {
                await OnFileHeaderAsync(env.From ?? "?", header);
                return;
            }
        }
        catch { /* tenta como chunk */ }

        try
        {
            var chunk = JsonMessageSerializer.Deserialize<FileChunk>(env.Payload);
            if (chunk?.Id is not null)
            {
                await OnFileChunkAsync(env.From ?? "?", env.To ?? "?", chunk);
                return;
            }
        }
        catch { }

        Console.WriteLine("[File] payload de arquivo desconhecido.");
    }

    private static Task OnFileHeaderAsync(string from, FileChunkHeader h)
    {
        // cria estado e arquivo de saída
        if (_recv.ContainsKey(h.Id))
            return Task.CompletedTask; // já existe (retransmissão)? ignora

        var safeName = string.Concat(h.FileName.Split(Path.GetInvalidFileNameChars()));
        var savePath = Path.Combine(DownloadsDir, $"recv_{h.Id}_{safeName}");

        var st = new FileRecvState(h.Id, h.FileName, savePath, h.TotalBytes, h.ChunkSize);
        _recv[h.Id] = st;

        Console.WriteLine($"[File] Recebendo de {from}: '{h.FileName}' ({h.TotalBytes:N0} bytes) → {savePath}");
        return Task.CompletedTask;
    }

    private static async Task OnFileChunkAsync(string from, string target, FileChunk c)
    {
        if (!_recv.TryGetValue(c.Id, out var st))
        {
            // chunk chegou antes do header? cria estado tardio (nome provisório)
            var savePath = Path.Combine(DownloadsDir, $"recv_{c.Id}_pending.bin");
            st = new FileRecvState(c.Id, "(desconhecido)", savePath, 0, 0);
            _recv[c.Id] = st;
        }

        await st.WriteAsync(c);

        // Progresso
        if (st.TotalBytes > 0)
        {
            var pct = (double)st.Received / st.TotalBytes * 100.0;
            Console.Write($"\r[File] {st.Received:N0}/{st.TotalBytes:N0} bytes ({pct:0.0}%)");
            if (st.Received >= st.TotalBytes) Console.WriteLine();
        }

        if (st.IsComplete)
        {
            await st.CloseAsync();
            Console.WriteLine($"[File] Arquivo salvo: {st.SavePath}");
            _recv.Remove(c.Id);
        }
    }

    // ===== Util =====

    private static string[] SplitN(string input, int n) =>
        input.Split(' ', n, StringSplitOptions.RemoveEmptyEntries);

    private static void PrintHelp()
    {
        Console.WriteLine("Comandos:");
        Console.WriteLine("  /msg <user> <texto>");
        Console.WriteLine("  /users");
        Console.WriteLine("  /group create <nome>");
        Console.WriteLine("  /group add <grupo> <user>");
        Console.WriteLine("  /gmsg <grupo> <texto>");
        Console.WriteLine("  /sendfile <user> <caminho>");
        Console.WriteLine("  /gfile <grupo> <caminho>");
        Console.WriteLine("  /quit");
    }

    private sealed class FileRecvState
    {
        public string Id { get; }
        public string Name { get; private set; }
        public string SavePath { get; private set; }
        public long TotalBytes { get; private set; }
        public int ChunkSize { get; private set; }
        public long Received { get; private set; }
        public int ExpectedCount { get; private set; }
        public int ReceivedCount { get; private set; }

        private FileStream _fs;

        public bool IsComplete => ExpectedCount > 0 && ReceivedCount >= ExpectedCount;

        public FileRecvState(string id, string name, string savePath, long totalBytes, int chunkSize)
        {
            Id = id;
            Name = name;
            SavePath = savePath;
            TotalBytes = totalBytes;
            ChunkSize = chunkSize;
            _fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            ExpectedCount = (totalBytes > 0 && chunkSize > 0) ? (int)Math.Ceiling((double)totalBytes / chunkSize) : 0;
        }

        public async Task WriteAsync(FileChunk c)
        {
            if (TotalBytes == 0 && ChunkSize == 0)
            {
                // estado tardio sem header — não sabemos o total. Apenas agrega sequencialmente.
                ExpectedCount = Math.Max(ExpectedCount, c.Count);
            }

            await _fs.WriteAsync(c.Data, 0, c.Data.Length);
            Received += c.Data.Length;
            ReceivedCount = Math.Max(ReceivedCount, c.Index + 1);

            if (ExpectedCount == 0) ExpectedCount = c.Count;
        }

        public async Task CloseAsync()
        {
            await _fs.FlushAsync();
            _fs.Close();
        }
    }
}
