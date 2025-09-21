using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;
using Chat.Server.Hub;

namespace Chat.Server;

internal static class Program
{
    private static Socket? _listener;

    public static async Task Main(string[] args)
    {
        int port = (args.Length > 0 && int.TryParse(args[0], out var p)) ? p : 5000;

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Any, port));
        _listener.Listen(backlog: 512);
        Console.WriteLine($"[Server] Escutando em 0.0.0.0:{port}");

        var sessions = new SessionManager();
        var groups = new GroupManager();
        var router = new Router(sessions, groups);

        Console.CancelKeyPress += (_, __) => { try { _listener?.Close(); } catch { } };

        _ = Task.Run(() => AcceptLoopAsync(_listener!, router, sessions));

        Console.WriteLine("[Server] Pressione Ctrl+C para encerrar.");
        await Task.Delay(-1);
    }

    private static async Task AcceptLoopAsync(Socket listener, Router router, SessionManager sessions)
    {
        while (true)
        {
            Socket? sock = null;
            try
            {
                sock = await listener.AcceptAsync();
                _ = Task.Run(() => HandleClientAsync(sock, router, sessions));
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server] erro em Accept: {ex.Message}");
                sock?.Dispose();
            }
        }
    }

    private static async Task HandleClientAsync(Socket socket, Router router, SessionManager sessions)
    {
        ClientSession? session = null;

        try
        {
            // 1) Primeira mensagem deve ser AUTH
            var first = await SocketFraming.ReadFrameAsync(socket);
            if (first is null) { socket.Dispose(); return; }

            var env = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(first));
            if (env is null || env.Type != MessageType.Auth)
            {
                await SendErrorAsync(socket, "AUTH_REQUIRED", "Primeira mensagem deve ser AUTH");
                socket.Dispose(); return;
            }

            var auth = JsonMessageSerializer.Deserialize<AuthRequest>(env.Payload);
            if (auth is null || string.IsNullOrWhiteSpace(auth.Username))
            {
                await SendErrorAsync(socket, "INVALID_AUTH", "Username inválido");
                socket.Dispose(); return;
            }

            if (!sessions.TryAdd(auth.Username, socket))
            {
                await SendErrorAsync(socket, "USERNAME_TAKEN", "Usuário já conectado");
                socket.Dispose(); return;
            }

            session = sessions.Get(auth.Username)!;
            Console.WriteLine($"[Server] {auth.Username} conectado.");

            await session.SendAsync(ProtocolUtil.Make(MessageType.Ack, "server", auth.Username, new AckMessage("login", "ok")));

            // 2) Loop de mensagens do cliente
            while (true)
            {
                var frame = await SocketFraming.ReadFrameAsync(socket);
                if (frame is null) break;

                var envelope = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(frame));
                if (envelope is null) continue;

                await router.HandleAsync(session, envelope);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Server] erro: {ex.Message}");
        }
        finally
        {
            if (session is not null)
            {
                sessions.Remove(session.Username);
                Console.WriteLine($"[Server] {session.Username} saiu.");
            }
            try { socket.Shutdown(SocketShutdown.Both); } catch { }
            socket.Dispose();
        }
    }

    private static async Task SendErrorAsync(Socket socket, string code, string message)
    {
        var env = ProtocolUtil.Make(MessageType.Error, "server", null, new ErrorMessage(code, message));
        var json = JsonSerializer.Serialize(env);
        await SocketFraming.SendFrameAsync(socket, Encoding.UTF8.GetBytes(json));
    }
}
