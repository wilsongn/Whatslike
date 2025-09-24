using System.Net;
using System.Net.Sockets;

namespace Chat.Server;

public sealed class SocketServer
{
    private readonly int _port;
    private readonly ConnectionTable _table;
    private readonly int _heartbeatSec;
    private readonly int _idleSec;

    public SocketServer(int port, ConnectionTable table, int heartbeatSec, int idleSec)
    {
        _port = port;
        _table = table;
        _heartbeatSec = heartbeatSec;
        _idleSec = idleSec;
    }

    public async Task StartAsync()
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Console.WriteLine($"[Server] Escutando em 0.0.0.0:{_port}");
        Console.CancelKeyPress += (_, __) => listener.Stop();

        while (true)
        {
            Socket s;
            try { s = await listener.AcceptSocketAsync(); }
            catch (ObjectDisposedException) { break; }

            var conn = new ClientConn(s, _table, _heartbeatSec, TimeSpan.FromSeconds(5));
            _table.Register(conn);
            conn.Start();
        }
    }
}
