using System.Text.Json;
using Confluent.Kafka;
using Chat.Persistence.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Chat.StatusWorker;

public class StatusWorkerService : BackgroundService
{
    private readonly ILogger<StatusWorkerService> _logger;
    private readonly IMessageStore _messageStore;
    private readonly IConnectionMultiplexer _redis;
    private readonly StatusWorkerOptions _options;

    public StatusWorkerService(
        ILogger<StatusWorkerService> logger,
        IMessageStore messageStore,
        IConnectionMultiplexer redis,
        StatusWorkerOptions options)
    {
        _logger = logger;
        _messageStore = messageStore;
        _redis = redis;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StatusWorker iniciando. Topic={Topic} Group={Group}",
            _options.TopicStatus, _options.GroupId);

        var config = new ConsumerConfig
        {
            BootstrapServers = _options.KafkaBootstrap,
            GroupId = _options.GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(_options.TopicStatus);

        var subscriber = _redis.GetSubscriber();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var consumeResult = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (consumeResult == null) continue;

                try
                {
                    var statusEvent = JsonSerializer.Deserialize<StatusEvent>(consumeResult.Message.Value);
                    if (statusEvent == null)
                    {
                        _logger.LogWarning("Evento de status nulo ou inválido");
                        consumer.Commit(consumeResult);
                        continue;
                    }

                    _logger.LogInformation(
                        "Status recebido: MessageId={MessageId} Status={Status} Channel={Channel}",
                        statusEvent.MessageId, statusEvent.Status, statusEvent.Channel);

                    // 1. Atualizar status no banco (se for READ)
                    if (statusEvent.Status.Equals("READ", StringComparison.OrdinalIgnoreCase))
                    {
                        await UpdateMessageStatusInDatabase(statusEvent, stoppingToken);
                    }

                    // 2. Notificar via Redis Pub/Sub (para WebSocket)
                    await NotifyViaWebSocket(statusEvent, subscriber, stoppingToken);

                    consumer.Commit(consumeResult);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao processar evento de status. Offset={Offset}",
                        consumeResult.Offset);
                    // Não faz commit em caso de erro
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("StatusWorker sendo encerrado");
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task UpdateMessageStatusInDatabase(StatusEvent statusEvent, CancellationToken ct)
    {
        try
        {
            // Você precisa ter essas informações no evento ou buscar do banco
            // Por simplicidade, vamos assumir que o evento tem essas infos
            if (statusEvent.OrganizacaoId == Guid.Empty || statusEvent.ConversaId == Guid.Empty)
            {
                _logger.LogWarning("Evento sem OrganizacaoId ou ConversaId. Pulando atualização no banco.");
                return;
            }

            var bucket = _messageStore.ComputeBucket(statusEvent.Timestamp);
            
            // Precisamos buscar a sequência da mensagem pelo message_id
            // Isso requer um índice secundário no Cassandra ou cache em Redis
            // Por enquanto, vamos logar
            _logger.LogInformation(
                "Atualizando status READ para mensagem {MessageId} (implementar busca de sequência)",
                statusEvent.MessageId);

            // TODO: Implementar busca de sequência e atualização
            // await _messageStore.UpdateMessageStatusAsync(
            //     statusEvent.OrganizacaoId, 
            //     statusEvent.ConversaId, 
            //     bucket, 
            //     sequencia, 
            //     "read");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao atualizar status no banco para mensagem {MessageId}",
                statusEvent.MessageId);
        }
    }

    private async Task NotifyViaWebSocket(StatusEvent statusEvent, ISubscriber subscriber, CancellationToken ct)
    {
        try
        {
            // Canal Redis específico para a conversa
            var channel = $"status:{statusEvent.ConversaId}";
            
            var notification = new WebSocketNotification
            {
                Type = "message.status",
                MessageId = statusEvent.MessageId,
                ConversationId = statusEvent.ConversaId,
                Status = statusEvent.Status,
                Channel = statusEvent.Channel,
                Timestamp = statusEvent.Timestamp
            };

            var json = JsonSerializer.Serialize(notification);
            await subscriber.PublishAsync(RedisChannel.Literal(channel), json);

            _logger.LogInformation(
                "Notificação WebSocket publicada no canal Redis: {Channel} para mensagem {MessageId}",
                channel, statusEvent.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao publicar notificação WebSocket via Redis");
        }
    }
}

// DTOs
public record StatusEvent(
    string MessageId,
    string Channel,
    string Status,
    DateTimeOffset Timestamp,
    Guid ConversaId = default,
    Guid OrganizacaoId = default
)
{
    // Para deserialização JSON com case-insensitive
    public string message_id
    {
        get => MessageId;
        init => MessageId = value;
    }
    public string channel
    {
        get => Channel;
        init => Channel = value;
    }
    public string status
    {
        get => Status;
        init => Status = value;
    }
    public DateTimeOffset timestamp
    {
        get => Timestamp;
        init => Timestamp = value;
    }
    public Guid conversation_id
    {
        get => ConversaId;
        init => ConversaId = value;
    }
    public Guid organizacao_id
    {
        get => OrganizacaoId;
        init => OrganizacaoId = value;
    }
}

public record WebSocketNotification
{
    public required string Type { get; init; }
    public required string MessageId { get; init; }
    public required Guid ConversationId { get; init; }
    public required string Status { get; init; }
    public required string Channel { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public class StatusWorkerOptions
{
    public required string KafkaBootstrap { get; init; }
    public required string TopicStatus { get; init; }
    public required string GroupId { get; init; }
}
