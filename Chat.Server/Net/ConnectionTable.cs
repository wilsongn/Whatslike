using System.Collections.Concurrent;
using System.Text.Json;
using Chat.Server.Distributed;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

namespace Chat.Server;

public sealed class ConnectionTable
{
    private readonly ConcurrentDictionary<string, ClientConn> _byUser = new();
    private readonly ConcurrentDictionary<Guid, ClientConn> _byId = new();

    private readonly IPresenceStore _presence;
    private readonly IGroupStore _groups;
    private readonly INodeBus _bus;
    private readonly string _nodeId;
    private readonly TimeSpan _presenceTtl;

    private static readonly bool Verbose =
    (Environment.GetEnvironmentVariable("DEMO_VERBOSE") ?? "true")
    .Equals("true", StringComparison.OrdinalIgnoreCase);

    public int ActiveCount => _byId.Count;


    public ConnectionTable(string nodeId, IPresenceStore presence, IGroupStore groups, INodeBus bus, TimeSpan presenceTtl)
    {
        _nodeId = nodeId;
        _presence = presence;
        _groups = groups;
        _bus = bus;
        _presenceTtl = presenceTtl;
    }

    public string NodeId => _nodeId;
    public IGroupStore Groups => _groups;
    public IPresenceStore Presence => _presence;

    public void Register(ClientConn c)
    {
        _byId[c.Id] = c;
        if (!string.IsNullOrWhiteSpace(c.Username))
            _byUser[c.Username] = c;
        Interlocked.Increment(ref Metrics.ConnectionsOpened);
    }

    public void Unregister(ClientConn c)
    {
        _byId.TryRemove(c.Id, out _);
        if (!string.IsNullOrWhiteSpace(c.Username))
        {
            _byUser.TryRemove(c.Username, out _);
            _ = _presence.RemoveAsync(c.Username);
        }
        Interlocked.Increment(ref Metrics.ConnectionsClosed);
    }


    public bool TryGetByUser(string user, out ClientConn? c) => _byUser.TryGetValue(user, out c);

    public async Task OnAuthAsync(ClientConn c, string username)
    {
        if (_byUser.TryGetValue(username, out var old) && old.Id != c.Id) old.Close("New session");
        c.Username = username;
        _byUser[username] = c;
        await _presence.SetAsync(username, _nodeId, _presenceTtl);
        if (Verbose) Console.WriteLine($"[Presence] set {username} -> {_nodeId} ttl={_presenceTtl.TotalSeconds:F0}s");
    }


    public Task RenewPresenceAsync(string username)
        => _presence.SetAsync(username, _nodeId, _presenceTtl);

    public async Task DeliverPrivateAsync(Envelope env)
    {
        Interlocked.Increment(ref Metrics.PrivateMsgs);

        if (!string.IsNullOrWhiteSpace(env.To) && TryGetByUser(env.To, out var local) && local is not null)
        {
            if (Verbose) Console.WriteLine($"[Route][private][local] {env.From} -> {env.To}");
            Interlocked.Increment(ref Metrics.LocalDeliveries);
            await local.SendAsync(env);
            return;
        }

        if (string.IsNullOrWhiteSpace(env.To)) return;
        var node = await _presence.GetNodeAsync(env.To);
        if (node is null)
        {
            if (TryGetByUser(env.From!, out var sender) && sender is not null)
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Error, "server", sender.Username,
                    new ErrorMessage("offline", $"Usuário {env.To} offline")));
            }
            return;
        }

        if (node == _nodeId) return; // já teria entregue local

        if (Verbose) Console.WriteLine($"[Route][private][remote] {env.From} -> {env.To} via node={node}");
        Interlocked.Increment(ref Metrics.RemotePublishes);
        var json = JsonSerializer.Serialize(env);
        await _bus.PublishAsync(node, Routed.Serialize(new Routed(_nodeId, node, json)));
    }


    public async Task DeliverGroupAsync(Envelope env, string group)
    {
        Interlocked.Increment(ref Metrics.GroupMsgs);

        var partitions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int localCount = 0;

        await foreach (var user in _groups.MembersAsync(group))
        {
            if (TryGetByUser(user, out var local) && local is not null)
            {
                await local.SendAsync(env);
                localCount++;
                continue;
            }
            var node = await _presence.GetNodeAsync(user);
            if (node is null) continue;
            (partitions.TryGetValue(node, out var list) ? list : partitions[node] = new()).Add(user);
        }

        if (localCount > 0)
        {
            Interlocked.Add(ref Metrics.LocalDeliveries, localCount);
            if (Verbose) Console.WriteLine($"[Route][group][local] {env.From} -> {group} members={localCount}");
        }

        if (partitions.Count == 0) return;

        var envJson = JsonSerializer.Serialize(env);
        foreach (var kv in partitions)
        {
            var targets = kv.Value.ToArray();
            if (Verbose) Console.WriteLine($"[Route][group][remote] {env.From} -> {group} node={kv.Key} targets={targets.Length}");
            Interlocked.Increment(ref Metrics.RemotePublishes);
            await _bus.PublishAsync(kv.Key, Routed.Serialize(new Routed(_nodeId, kv.Key, envJson, targets)));
        }
    }


    public async Task DeliverFromBusAsync(Envelope env, string[]? targets)
    {
        if (env.Type == MessageType.PrivateMsg && !string.IsNullOrWhiteSpace(env.To))
        {
            if (TryGetByUser(env.To, out var c) && c is not null)
            {
                Interlocked.Increment(ref Metrics.BusDelivered);
                Interlocked.Increment(ref Metrics.LocalDeliveries);
                if (Verbose) Console.WriteLine($"[Bus][in][private] {env.From} -> {env.To}");
                await c.SendAsync(env);
            }
            return;
        }

        if (env.Type == MessageType.GroupMsg && !string.IsNullOrWhiteSpace(env.To))
        {
            int delivered = 0;
            if (targets is not null && targets.Length > 0)
            {
                foreach (var u in targets)
                    if (TryGetByUser(u, out var c) && c is not null)
                    {
                        await c.SendAsync(env);
                        delivered++;
                    }
            }
            Interlocked.Add(ref Metrics.BusDelivered, delivered);
            Interlocked.Add(ref Metrics.LocalDeliveries, delivered);
            if (Verbose) Console.WriteLine($"[Bus][in][group] {env.From} -> {env.To} delivered={delivered}");
        }
    }

}
