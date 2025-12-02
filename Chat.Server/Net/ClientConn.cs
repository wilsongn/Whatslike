using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Chat.Shared.Net;
using Chat.Shared.Protocol;
using Chat.Server.Telemetry;
using Prometheus; // <- para usar Telemetry.*


namespace Chat.Server;

public sealed class ClientConn
{
    public Guid Id { get; } = Guid.NewGuid();
    public string? Username { get; set; }
    public DateTime LastSeenUtc { get; private set; } = DateTime.UtcNow;
    private int _closed = 0;

    private readonly Socket _socket;
    private readonly Channel<Envelope> _sendQ;
    private readonly ConnectionTable _table;
    private readonly CancellationTokenSource _cts = new();

    private readonly TimeSpan _authTimeout;
    private readonly int _heartbeatSec;

    private static readonly bool LogPing =
    (Environment.GetEnvironmentVariable("PING_LOG") ?? "false")
    .Equals("true", StringComparison.OrdinalIgnoreCase);


    public ClientConn(Socket socket, ConnectionTable table, int heartbeatSec, TimeSpan authTimeout)
    {
        _socket = socket;
        _table = table;
        _heartbeatSec = heartbeatSec;
        _authTimeout = authTimeout;
        _sendQ = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(100)
        {
            SingleWriter = false,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public void Start()
    {
        Chat.Server.Telemetry.Telemetry.SocketSessionsOpened.Inc();   // [METRICS]
        Chat.Server.Telemetry.Telemetry.ActiveSessions.Inc();         // [METRICS]

        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(SendLoopAsync);
        _ = Task.Run(AuthWatchdogAsync);
        _ = Task.Run(PresencePumperAsync);
    }


    public async Task SendAsync(Envelope env)
    {
        if (!_sendQ.Writer.TryWrite(env))
        {
            Close("Backpressure");
        }
    }

    public void Close(string reason)
    {
        // A MÁGICA: Se _closed já era 1, retorna e não faz nada.
        // Se era 0, vira 1 e continua. Isso garante execução única.
        if (Interlocked.Exchange(ref _closed, 1) == 1) 
        {
            return; 
        }

        try { _cts.Cancel(); } catch { }
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket.Dispose(); } catch { }
        _table.Unregister(this);

        // Agora isso só roda uma vez por usuário!
        Chat.Server.Telemetry.Telemetry.SocketSessionsClosed.Inc();
        Chat.Server.Telemetry.Telemetry.ActiveSessions.Dec();

        Console.WriteLine($"[Conn] {Username ?? Id.ToString()} closed: {reason}");
    }


    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var env in _sendQ.Reader.ReadAllAsync(_cts.Token))
            {
                Chat.Server.Telemetry.Telemetry.MessagesOut.WithLabels(env.Type.ToString()).Inc();  // [METRICS]

                var json = JsonSerializer.Serialize(env);
                await SocketFraming.SendFrameAsync(_socket, Encoding.UTF8.GetBytes(json));
            }
        }
        catch { Close("SendLoop"); }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await SocketFraming.ReadFrameAsync(_socket, _cts.Token);
                if (frame is null) break;

                LastSeenUtc = DateTime.UtcNow;

                var env = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(frame));
                if (env is null) continue;

                Chat.Server.Telemetry.Telemetry.MessagesIn.WithLabels(env.Type.ToString()).Inc();   // [METRICS]

                await HandleAsync(env);
            }
        }
        catch { }
        finally { Close("ReceiveLoop end"); }
    }

    private async Task HandleAsync(Envelope env)
    {
        switch (env.Type)
        {
            case MessageType.Auth:
                {
                    var req = JsonMessageSerializer.Deserialize<AuthRequest>(env.Payload);
                    if (req is null || string.IsNullOrWhiteSpace(req.Username))
                    {
                        await SendAsync(ProtocolUtil.Make(
                            MessageType.Error, "server", null,
                            new ErrorMessage("auth", "username inválido")));
                        Close("BadAuth");
                        return;
                    }
                    await _table.OnAuthAsync(this, req.Username);
                    await SendAsync(ProtocolUtil.Make(
                        MessageType.Ack, "server", Username,
                        new AckMessage("login", "ok")));
                    break;
                }
            case MessageType.Ping:
                if (Username is not null)
                    await _table.RenewPresenceAsync(Username);

                if (LogPing) Console.WriteLine($"[Ping] <- {Username} {DateTime.UtcNow:HH:mm:ss}");

                await SendAsync(ProtocolUtil.Make(
                    MessageType.Pong, "server", Username, new Pong()));

                if (LogPing) Console.WriteLine($"[Pong] -> {Username}");
                break;

            case MessageType.PrivateMsg:
                using (Chat.Server.Telemetry.Telemetry.DeliveryLatency.WithLabels("private").NewTimer()) // [METRICS]
                    await _table.DeliverPrivateAsync(env);
                break;

            case MessageType.GroupMsg:
                using (Chat.Server.Telemetry.Telemetry.DeliveryLatency.WithLabels("group").NewTimer())   // [METRICS]
                    await _table.DeliverGroupAsync(env, env.To!);
                break;

            case MessageType.CreateGroup:
                {
                    var rq = JsonMessageSerializer.Deserialize<CreateGroupRequest>(env.Payload);
                    if (rq is null) return;
                    await _table.Groups.CreateAsync(rq.Name);
                    await SendAsync(ProtocolUtil.Ack($"grupo {rq.Name} criado"));
                    break;
                }
            case MessageType.AddToGroup:
                {
                    var rq = JsonMessageSerializer.Deserialize<AddToGroupRequest>(env.Payload);
                    if (rq is null) return;
                    if (!await _table.Groups.ExistsAsync(rq.Name))
                    {
                        await SendAsync(ProtocolUtil.Error("grupo", $"grupo {rq.Name} não existe"));
                        return;
                    }
                    await _table.Groups.AddAsync(rq.Name, rq.Username);
                    await SendAsync(ProtocolUtil.Ack($"adicionado {rq.Username}"));
                    break;
                }

            case MessageType.FileChunk:
                Interlocked.Increment(ref Metrics.FileChunksForwarded);
                Chat.Server.Telemetry.Telemetry.FileChunks.Inc();                             // [METRICS]
                // sua lógica: privado OU grupo (conforme env.To)
                await _table.DeliverPrivateAsync(env); // ou DeliverGroupAsync se To == grupo
                break;

            default:
                await SendAsync(ProtocolUtil.Error("tipo", $"não suportado: {env.Type}"));
                break;
        }
    }

    private async Task AuthWatchdogAsync()
    {
        var start = DateTime.UtcNow;
        while (Username is null && !_cts.IsCancellationRequested)
        {
            if (DateTime.UtcNow - start > _authTimeout)
            {
                Close("AuthTimeout");
                return;
            }
            await Task.Delay(200);
        }
    }

    private async Task PresencePumperAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (Username != null)
            {
                var idle = DateTime.UtcNow - LastSeenUtc;
                if (idle > TimeSpan.FromSeconds(300))
                {
                    Close("IdleTimeout");
                    return;
                }
            }
            await Task.Delay(1000);
        }
    }
}
