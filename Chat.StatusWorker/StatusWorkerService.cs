using System.Text.Json;
using System.Text.Json.Serialization;
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

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
                    _logger.LogDebug("Raw status message: {Raw}", consumeResult.Message.Value);

                    var statusEvent = JsonSerializer.Deserialize<StatusEvent>(consumeResult.Message.Value, _jsonOptions);
                    if (statusEvent == null)
                    {
                        _logger.LogWarning("Evento de status nulo ou inválido");
                        consumer.Commit(consumeResult);
                        continue;
                    }

                    _logger.LogInformation(
                        "Status recebido: MessageId={MessageId} Status={Status} Channel={Channel} ConversationId={ConversationId}",
                        statusEvent.MessageId, statusEvent.Status, statusEvent.Channel, statusEvent.ConversationId);

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
                    _logger.LogError(ex, "Erro ao processar evento de status. Offset={Offset} Raw={Raw}",
                        consumeResult.Offset, consumeResult.Message.Value);
                    // Commit para não ficar em loop infinito
                    consumer.Commit(consumeResult);
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
            if (statusEvent.OrganizationId == Guid.Empty || statusEvent.ConversationId == Guid.Empty)
            {
                _logger.LogWarning("Evento sem OrganizationId ou ConversationId. Pulando atualização no banco.");
                return;
            }

            var bucket = _messageStore.ComputeBucket(statusEvent.Timestamp);

            _logger.LogInformation(
                "Atualizando status READ para mensagem {MessageId}",
                statusEvent.MessageId);
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
            var channel = $"status:{statusEvent.ConversationId}";

            var notification = new WebSocketNotification
            {
                Type = "message.status",
                MessageId = statusEvent.MessageId,
                ConversationId = statusEvent.ConversationId,
                Status = statusEvent.Status,
                Channel = statusEvent.Channel,
                Timestamp = statusEvent.Timestamp
            };

            var json = JsonSerializer.Serialize(notification, _jsonOptions);
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

// DTOs - Corrigido para não ter propriedades duplicadas
public class StatusEvent
{
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("conversationId")]
    public Guid ConversationId { get; set; }

    [JsonPropertyName("organizationId")]
    public Guid OrganizationId { get; set; }
}

public class WebSocketNotification
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("conversationId")]
    public required Guid ConversationId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }
}

public class StatusWorkerOptions
{
    public required string KafkaBootstrap { get; init; }
    public required string TopicStatus { get; init; }
    public required string GroupId { get; init; }
}