using System.Collections.Concurrent;

namespace Chat.Server.Hub
{
    public class GroupManager
    {
        private readonly ConcurrentDictionary<string, HashSet<string>> _groups = new();

        public bool Create(string name) => _groups.TryAdd(name, new HashSet<string>());

        public bool AddUser(string name, string username)
        {
            if (!_groups.TryGetValue(name, out var set))
                return false;
            lock (set) return set.Add(username);
        }

        public IEnumerable<string> GetMembers(string name) =>
            _groups.TryGetValue(name, out var set) ? set : Enumerable.Empty<string>();

        public IEnumerable<string> ListGroups() => _groups.Keys;
    }
}
