using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Chat.Server.Hub
{
    public class SessionManager
    {
        private readonly ConcurrentDictionary<string, ClientSession> _byUser = new();

        public bool TryAdd(string username, Socket socket) =>
            _byUser.TryAdd(username, new ClientSession(username, socket));

        public void Remove(string username) => _byUser.TryRemove(username, out _);

        public ClientSession? Get(string username) =>
            _byUser.TryGetValue(username, out var s) ? s : null;

        public IEnumerable<string> ListUsers() => _byUser.Keys;
    }
}
