using StackExchange.Redis;

namespace Chat.Server.Distributed;

public sealed class RedisGroupStore : IGroupStore
{
    private readonly IDatabase _db;
    public RedisGroupStore(ConnectionMultiplexer mux) => _db = mux.GetDatabase();

    static string GKey(string name) => $"group:{name}:members";
    static string GExistsKey(string name) => $"group:{name}:exists";

    public Task CreateAsync(string name)
        => _db.StringSetAsync(GExistsKey(name), "1");

    public Task<bool> ExistsAsync(string name)
        => _db.KeyExistsAsync(GExistsKey(name));

    public Task AddAsync(string name, string user)
        => _db.SetAddAsync(GKey(name), user);

    public async IAsyncEnumerable<string> MembersAsync(string name)
    {
        var members = await _db.SetMembersAsync(GKey(name));
        foreach (var m in members) yield return m.ToString();
    }
}
