using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

public interface IMessagePublisher
{
    Task PublishAsync(MessageProducedEvent evt, CancellationToken ct = default);
}

public sealed class MessagePublisher : IMessagePublisher
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaOptions _opt;
    public MessagePublisher(IProducer<string, string> producer, IOptions<KafkaOptions> opt)
    {
        _producer = producer; _opt = opt.Value;
    }

    public async Task PublishAsync(MessageProducedEvent evt, CancellationToken ct = default)
    {
        var key = evt.ConversaId.ToString(); // ordenação por conversa
        var val = JsonSerializer.Serialize(evt);
        var msg = new Message<string, string>
        {
            Key = key,
            Value = val,
            Headers = new Headers {
            new Header("tenant_id", System.Text.Encoding.UTF8.GetBytes(evt.OrganizacaoId.ToString()))
        }
        };
        var dr = await _producer.ProduceAsync(_opt.TopicMessages, msg, ct);
        // opcional: log do offset: dr.TopicPartitionOffset
    }
}
