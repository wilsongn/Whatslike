using Cassandra;
using Chat.Persistence.Options;
using Microsoft.Extensions.Options;

namespace Chat.Persistence.Internal;

internal sealed class CassandraSessionFactory
{
    private readonly CassandraOptions _opt;
    private ICluster? _cluster;
    private ISession? _session;

    public CassandraSessionFactory(IOptions<CassandraOptions> opt) => _opt = opt.Value;

    public async Task<ISession> GetSessionAsync(CancellationToken ct = default)
    {
        if (_session != null) return _session;

        _cluster = Cluster.Builder()
            .AddContactPoints(_opt.ContactPoints)
            .WithPort(_opt.Port)
            .WithLoadBalancingPolicy(new Cassandra.DefaultLoadBalancingPolicy(_opt.DataCenter))
            .Build();

        var session = await _cluster.ConnectAsync().ConfigureAwait(false);

        // Keyspace
        var repl =
            _opt.ReplicationClass.Equals("NetworkTopologyStrategy", StringComparison.OrdinalIgnoreCase)
            ? $"'class': 'NetworkTopologyStrategy', '{_opt.DataCenter}': '{_opt.ReplicationFactor}'"
            : $"'class': 'SimpleStrategy', 'replication_factor': '{_opt.ReplicationFactor}'";

        await session.ExecuteAsync(new SimpleStatement(
            $"CREATE KEYSPACE IF NOT EXISTS {_opt.Keyspace} WITH replication = {{{repl}}} AND durable_writes = true;"));

        _session = await _cluster.ConnectAsync(_opt.Keyspace).ConfigureAwait(false);
        return _session;
    }
}
