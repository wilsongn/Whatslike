using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography; // Necessário para gerar IDs compatíveis com o banco
using System.Text;
using Confluent.Kafka;            
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
    private readonly IProducer<string, string> _kafkaProducer; // <--- NOVO

    private static readonly bool Verbose =
        (Environment.GetEnvironmentVariable("DEMO_VERBOSE") ?? "true")
        .Equals("true", StringComparison.OrdinalIgnoreCase);

    public int ActiveCount => _byId.Count;

    // Construtor atualizado recebendo o KafkaProducer
    public ConnectionTable(
        string nodeId,
        IPresenceStore presence,
        IGroupStore groups,
        INodeBus bus,
        TimeSpan presenceTtl,
        IProducer<string, string> kafkaProducer)
    {
        _nodeId = nodeId;
        _presence = presence;
        _groups = groups;
        _bus = bus;
        _presenceTtl = presenceTtl;
        _kafkaProducer = kafkaProducer;
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

    public IEnumerable<string> GetOnlineUsers() => _byUser.Keys;

    public async Task OnAuthAsync(ClientConn c, string username)
    {
        if (_byUser.TryGetValue(username, out var old) && old.Id != c.Id) old.Close("New session");
        c.Username = username;
        _byUser[username] = c;
        await _presence.SetAsync(username, _nodeId, _presenceTtl);
        if (Verbose) Console.WriteLine($"[Presence] set {username} -> {_nodeId}");
    }

    public Task RenewPresenceAsync(string username)
        => _presence.SetAsync(username, _nodeId, _presenceTtl);

    public async Task DeliverPrivateAsync(Envelope env)
    {
        if (string.IsNullOrWhiteSpace(env.To)) return;
        Interlocked.Increment(ref Metrics.PrivateMsgs);

        // 1. Tenta entrega local (Usuário conectado neste servidor)
        if (TryGetByUser(env.To, out var local) && local is not null)
        {
            if (Verbose) Console.WriteLine($"[Route][local] {env.From} -> {env.To}");
            Interlocked.Increment(ref Metrics.LocalDeliveries);
            await local.SendAsync(env);
            
            return;
        }

        // 2. Busca onde o usuário está (Redis)
        var node = await _presence.GetNodeAsync(env.To);

        // 3. SE O USUÁRIO ESTÁ OFFLINE (node é null)
        if (node is null)
        {
            if (Verbose) Console.WriteLine($"[Route][offline] {env.To} desconectado. Salvando no Kafka...");
            
            // Persiste para entrega futura
            await PersistToKafkaAsync(env);

            // Avisa quem enviou que foi "enfileirado" (Ack) em vez de Erro
            if (TryGetByUser(env.From!, out var sender))
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Ack, "server", sender.Username,
                    new AckMessage("offline", $"Mensagem salva para {env.To}")));
            }
            return;
        }

        // 4. Entrega Remota (Usuário em outro servidor)
        if (node == _nodeId) return; 

        if (Verbose) Console.WriteLine($"[Route][remote] {env.From} -> {env.To} via node={node}");
        Interlocked.Increment(ref Metrics.RemotePublishes);
        var json = JsonSerializer.Serialize(env);
        await _bus.PublishAsync(node, Routed.Serialize(new Routed(_nodeId, node, json)));
    }

    // Método auxiliar para criar o evento que o RouterWorker entende
    private async Task PersistToKafkaAsync(Envelope env)
    {
        try 
        {
            var p1 = env.From ?? "anon";
            var p2 = env.To ?? "anon";
            var sala = string.CompareOrdinal(p1, p2) < 0 ? $"{p1}:{p2}" : $"{p2}:{p1}";
            var idConversa = StringToGuid(sala);
            var idRemetente = StringToGuid(env.From ?? "anon");
            var idOrg = StringToGuid("default-org");

            // Cria o evento igual ao que a API cria
            var evento = new
            {
                OrganizacaoId = idOrg,
                ConversaId = idConversa,
                MensagemId = Guid.NewGuid(),
                UsuarioRemetenteId = idRemetente,
                Direcao = "inbound",
                Canal = "chat-tcp",
                ConteudoJson = env.Payload, // O JSON original {"to":"bob","text":"oi"}
                CriadoEm = DateTimeOffset.UtcNow
            };

            var jsonKafka = JsonSerializer.Serialize(evento);

            // Publica no tópico que o Worker está escutando
            await _kafkaProducer.ProduceAsync("messages", new Message<string, string>
            {
                Key = idConversa.ToString(),
                Value = jsonKafka
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Kafka Error] {ex.Message}");
        }
    }

    private static Guid StringToGuid(string value)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }

    public async Task DeliverGroupAsync(Envelope env, string group)
    {
        Interlocked.Increment(ref Metrics.GroupMsgs);
        var partitions = new Dictionary<string, List<string>>();
        await foreach (var user in _groups.MembersAsync(group))
        {
            if (TryGetByUser(user, out var local)) { await local.SendAsync(env); continue; }
            var node = await _presence.GetNodeAsync(user);
            if (node != null) (partitions.TryGetValue(node, out var l) ? l : partitions[node] = new()).Add(user);
        }
        foreach(var kv in partitions)
            await _bus.PublishAsync(kv.Key, Routed.Serialize(new Routed(_nodeId, kv.Key, JsonSerializer.Serialize(env), kv.Value.ToArray())));
    }

    public async Task DeliverFromBusAsync(Envelope env, string[]? targets)
    {
        if (env.Type == MessageType.PrivateMsg && !string.IsNullOrWhiteSpace(env.To) && TryGetByUser(env.To, out var c))
            await c.SendAsync(env);
    }
}
