using StackExchange.Redis;

namespace Chat.Server.Distributed;

public sealed class RedisBus : INodeBus
{
    private readonly ISubscriber _sub;
    public string NodeId { get; }

    public RedisBus(ConnectionMultiplexer mux, string nodeId)
    {
        _sub = mux.GetSubscriber();
        NodeId = nodeId;
    }

    static string Channel(string node) => $"route:{node}";

    public Task PublishAsync(string node, string json)
        => _sub.PublishAsync(Channel(node), json);

    public void Subscribe(Action<string> onMessage)
        => _sub.Subscribe(Channel(NodeId), (_, msg) => onMessage(msg!));

    public void Dispose() => _sub.UnsubscribeAll();
}
