using StackExchange.Redis;

namespace Chat.Server.Distributed;

public sealed class RedisPresenceStore : IPresenceStore
{
    private readonly IDatabase _db;
    public RedisPresenceStore(ConnectionMultiplexer mux) => _db = mux.GetDatabase();
    static string Key(string user) => $"presence:{user}";

    public Task SetAsync(string user, string node, TimeSpan ttl)
        => _db.StringSetAsync(Key(user), node, ttl);

    public async Task<string?> GetNodeAsync(string user)
    {
        var v = await _db.StringGetAsync(Key(user));
        return v.HasValue ? v.ToString() : null;
    }

    public Task RemoveAsync(string user) => _db.KeyDeleteAsync(Key(user));
}
