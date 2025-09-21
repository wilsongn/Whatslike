using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

namespace Chat.Server.Hub
{
    public class ClientSession
    {
        public string Username { get; }
        public Socket Socket { get; }
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public ClientSession(string username, Socket socket)
        {
            Username = username;
            Socket = socket;
        }

        public async Task SendAsync(Envelope env, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(env);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(ct);
            try
            {
                await SocketFraming.SendFrameAsync(Socket, bytes, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
