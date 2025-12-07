using Confluent.Kafka;
using Confluent.Kafka.Admin;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ===== Config =====
const string ChannelName = "whatsapp";
string kafkaBootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ??
                        Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ??
                        "kafka:9092";
string groupId = Environment.GetEnvironmentVariable("GROUP_ID") ?? $"connector-{ChannelName}-mock";
string topicOut = Environment.GetEnvironmentVariable("TOPIC_OUT") ?? $"msg.out.{ChannelName}";
string topicIn = Environment.GetEnvironmentVariable("TOPIC_IN") ?? $"msg.in.{ChannelName}";
string topicStatus = Environment.GetEnvironmentVariable("TOPIC_STATUS") ?? "msg.status";
string callbackUrl = Environment.GetEnvironmentVariable("CALLBACK_URL") ?? "http://localhost:5000/v1/callbacks/status";
string? jwt = Environment.GetEnvironmentVariable("CONNECTOR_JWT");

var needed = new[] { topicOut, topicIn, topicStatus };
using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafkaBootstrap }).Build();
var meta = admin.GetMetadata(TimeSpan.FromSeconds(5));
var existing = meta.Topics.Select(t => t.Topic).ToHashSet(StringComparer.Ordinal);
var missing = needed.Where(t => !existing.Contains(t)).Distinct().ToList();
if (missing.Count > 0)
{
    var specs = missing.Select(t => new TopicSpecification { Name = t, NumPartitions = 3, ReplicationFactor = 1 }).ToList();
    try { await admin.CreateTopicsAsync(specs); Console.WriteLine("[KAFKA] topics created: " + string.Join(", ", missing)); }
    catch (CreateTopicsException e) { if (!e.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists)) throw; }
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IProducer<string, string>>(_ =>
{
    var cfg = new ProducerConfig
    {
        BootstrapServers = kafkaBootstrap,
        EnableIdempotence = true
    };
    return new ProducerBuilder<string, string>(cfg).Build();
});

builder.Services.AddSingleton<IConsumer<string, string>>(_ =>
{
    var cfg = new ConsumerConfig
    {
        BootstrapServers = kafkaBootstrap,
        GroupId = groupId,
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoCommit = false
    };
    return new ConsumerBuilder<string, string>(cfg).Build();
});

builder.Services.AddHostedService(sp => new OutboundConsumer(
    sp.GetRequiredService<IConsumer<string, string>>(),
    sp.GetRequiredService<IProducer<string, string>>(),
    topicOut, topicStatus, callbackUrl, jwt, ChannelName
));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/healthz", () => Results.Ok(new { ok = true, channel = ChannelName }));

app.MapPost("/mock/incoming", async (IncomingDto dto, IProducer<string, string> prod) =>
{
    var evt = new
    {
        messageId = Guid.NewGuid().ToString(),
        channel = ChannelName,
        from = dto.From,
        to = dto.To,
        text = dto.Text,
        conversationId = dto.ConversationId,
        timestamp = DateTimeOffset.UtcNow
    };
    var json = JsonSerializer.Serialize(evt);
    await prod.ProduceAsync(topicIn, new Message<string, string>
    {
        Key = dto.ConversationId ?? dto.To ?? Guid.NewGuid().ToString(),
        Value = json
    });
    return Results.Json(
        new { status = "queued", topic = topicIn },
        statusCode: StatusCodes.Status202Accepted
    );
});

app.Run();

// ======= tipos/worker =======
record IncomingDto(string? From, string? To, string Text, string? ConversationId);

sealed class OutboundConsumer : BackgroundService
{
    private readonly IConsumer<string, string> _consumer;
    private readonly IProducer<string, string> _producer;
    private readonly string _topicOut;
    private readonly string _topicStatus;
    private readonly string _callbackUrl;
    private readonly string? _jwt;
    private readonly string _channel;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OutboundConsumer(
        IConsumer<string, string> consumer,
        IProducer<string, string> producer,
        string topicOut,
        string topicStatus,
        string callbackUrl,
        string? jwt,
        string channel)
    {
        _consumer = consumer;
        _producer = producer;
        _topicOut = topicOut;
        _topicStatus = topicStatus;
        _callbackUrl = callbackUrl;
        _jwt = jwt;
        _channel = channel;
        _http = new HttpClient();
        if (!string.IsNullOrEmpty(_jwt))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwt);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _consumer.Subscribe(_topicOut);
        Console.WriteLine($"[{_channel.ToUpper()}-MOCK] subscribed: '{_topicOut}'");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cr = _consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (cr is null) continue;

                Console.WriteLine($"[{_channel.ToUpper()}] Raw message: {cr.Message.Value}");

                var payload = JsonSerializer.Deserialize<OutboundDto>(cr.Message.Value, _jsonOptions);
                if (payload is null)
                {
                    Console.WriteLine($"[{_channel.ToUpper()}] Failed to deserialize message");
                    _consumer.Commit(cr);
                    continue;
                }

                Console.WriteLine($"[{_channel.ToUpper()}] -> MessageId={payload.MessageId} Conv={payload.ConversationId} Org={payload.OrganizationId}");

                // Validar que temos os IDs necessários
                if (payload.ConversationId == Guid.Empty)
                {
                    Console.WriteLine($"[{_channel.ToUpper()}] WARNING: ConversationId is empty!");
                }

                // 1) SENT - imediato
                await EmitStatusAsync(payload.MessageId, "SENT", payload.ConversationId, payload.OrganizationId, stoppingToken);

                // 2) DELIVERED - após 500ms
                await Task.Delay(500, stoppingToken);
                await EmitStatusAsync(payload.MessageId, "DELIVERED", payload.ConversationId, payload.OrganizationId, stoppingToken);

                // 3) READ - após mais 1s
                await Task.Delay(1000, stoppingToken);
                await EmitStatusAsync(payload.MessageId, "READ", payload.ConversationId, payload.OrganizationId, stoppingToken);

                _consumer.Commit(cr);
            }
            catch (ConsumeException e)
            {
                Console.WriteLine($"[KAFKA] consume error: {e.Error.Reason}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[{_channel.ToUpper()}] Error processing message: {e.Message}");
            }
        }
    }

    private async Task EmitStatusAsync(
        string messageId,
        string status,
        Guid conversationId,
        Guid organizationId,
        CancellationToken ct)
    {
        // Usar camelCase consistente
        var evt = new
        {
            messageId = messageId,
            channel = _channel,
            status = status,
            timestamp = DateTimeOffset.UtcNow,
            conversationId = conversationId,
            organizationId = organizationId
        };
        var json = JsonSerializer.Serialize(evt);

        Console.WriteLine($"[{_channel.ToUpper()}] Emitting status: {status} for {messageId} conv={conversationId}");

        // (A) publica no tópico de status
        try
        {
            var result = await _producer.ProduceAsync(_topicStatus, new Message<string, string>
            {
                Key = messageId,
                Value = json
            }, ct);
            Console.WriteLine($"[KAFKA] Status published to {_topicStatus}: {status} partition={result.Partition} offset={result.Offset}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KAFKA] Failed to publish status: {ex.Message}");
        }

        // (B) envia callback HTTP (opcional, pode falhar)
        try
        {
            var res = await _http.PostAsJsonAsync(_callbackUrl, evt, ct);
            Console.WriteLine($"[CALLBACK] {status} -> {(int)res.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CALLBACK] failed (non-critical): {ex.Message}");
        }
    }
}

// DTO com suporte a camelCase (case-insensitive)
public class OutboundDto
{
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("conversationId")]
    public Guid ConversationId { get; set; }

    [JsonPropertyName("organizationId")]
    public Guid OrganizationId { get; set; }

    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}