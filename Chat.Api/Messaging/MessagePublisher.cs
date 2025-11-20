using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

public interface IMessagePublisher
{
    Task PublishAsync(MessageProducedEvent evt, CancellationToken ct = default);
}

public sealed class KafkaMessagePublisher : IMessagePublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;
    private static readonly HashSet<string> AllowedChannels =
        new(StringComparer.OrdinalIgnoreCase) { "whatsapp", "instagram" };

    public KafkaMessagePublisher(string bootstrapServers)
    {
        var cfg = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            EnableIdempotence = true,
            SecurityProtocol = SecurityProtocol.Plaintext
        };
        _producer = new ProducerBuilder<string, string>(cfg).Build();
    }

    public async Task PublishAsync(MessageProducedEvent evt, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(evt.Canal))
            throw new ArgumentException("Canal obrigatório.", nameof(evt));

        var channel = evt.Canal.Trim().ToLowerInvariant();
        if (!AllowedChannels.Contains(channel))
            throw new ArgumentException($"Canal inválido: {evt.Canal}.", nameof(evt));

        var topic = $"msg.out.{channel}";
        var json = JsonSerializer.Serialize(new
        {
            message_id = evt.MensagemId,
            channel = channel,
            to = (string?)null,           // se aplicar no seu domínio
            text = (string?)null,         // idem — o seu Frontend pode montar aqui
            file_id = (string?)null,      // se houver anexo
            conversation_id = evt.ConversaId.ToString(),
            tenant_id = evt.OrganizacaoId.ToString(),
            from_user_id = evt.UsuarioRemetenteId.ToString(),
            direction = evt.Direcao,
            payload = evt.ConteudoJson,
            timestamp = evt.CriadoEm
        });

        // chave: conversa (para manter ordenação por partição no mesmo canal)
        var key = evt.ConversaId.ToString();

        var dr = await _producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = key,
            Value = json
        }, ct);

        // opcional: log/local metrics
        // Console.WriteLine($"[Kafka] {topic}@{dr.Partition}:{dr.Offset} key={key}");
    }

    public void Dispose() => _producer.Dispose();
}
