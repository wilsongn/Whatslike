namespace Chat.Server.Distributed;

public interface IPresenceStore
{
    Task SetAsync(string user, string node, TimeSpan ttl);
    Task<string?> GetNodeAsync(string user);
    Task RemoveAsync(string user);
}

public interface IGroupStore
{
    Task CreateAsync(string name);
    Task AddAsync(string name, string user);
    Task<bool> ExistsAsync(string name);
    IAsyncEnumerable<string> MembersAsync(string name);
}

public interface INodeBus : IDisposable
{
    string NodeId { get; }
    Task PublishAsync(string node, string json);
    void Subscribe(Action<string> onMessage);
}
