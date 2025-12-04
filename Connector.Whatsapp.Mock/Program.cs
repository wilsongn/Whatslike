using Confluent.Kafka;
using Confluent.Kafka.Admin;
using System.Net.Http.Json;
using System.Text.Json;

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
        message_id = Guid.NewGuid().ToString("N"),
        channel = ChannelName,
        from = dto.From,
        to = dto.To,
        text = dto.Text,
        conversation_id = dto.ConversationId,
        timestamp = DateTime.UtcNow
    };
    var json = JsonSerializer.Serialize(evt);
    await prod.ProduceAsync(topicIn, new Message<string, string>
    {
        Key = dto.ConversationId ?? dto.To ?? Guid.NewGuid().ToString("N"),
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

                var payload = JsonSerializer.Deserialize<OutboundDto>(cr.Message.Value);
                if (payload is null) { _consumer.Commit(cr); continue; }

                Console.WriteLine($"[{_channel.ToUpper()}] -> {payload.to} | {(payload.text ?? payload.file_id)} | Conv={payload.conversation_id} Org={payload.organizacao_id}");

                // 1) SENT
                await EmitStatusAsync(payload.message_id, "SENT", payload.conversation_id, payload.organizacao_id, stoppingToken);
                // 2) DELIVERED
                await Task.Delay(400, stoppingToken);
                await EmitStatusAsync(payload.message_id, "DELIVERED", payload.conversation_id, payload.organizacao_id, stoppingToken);
                // 3) READ
                await Task.Delay(800, stoppingToken);
                await EmitStatusAsync(payload.message_id, "READ", payload.conversation_id, payload.organizacao_id, stoppingToken);

                _consumer.Commit(cr);
            }
            catch (ConsumeException e)
            {
                Console.WriteLine($"[KAFKA] consume error: {e.Error.Reason}");
            }
        }
    }

    private async Task EmitStatusAsync(
        string messageId, 
        string status, 
        Guid conversationId, 
        Guid organizacaoId, 
        CancellationToken ct)
    {
        var evt = new
        {
            message_id = messageId,
            channel = _channel,
            status,
            timestamp = DateTime.UtcNow,
            conversation_id = conversationId,
            organizacao_id = organizacaoId
        };
        var json = JsonSerializer.Serialize(evt);

        // (A) publica no tópico de status
        await _producer.ProduceAsync(_topicStatus, new Message<string, string>
        {
            Key = messageId,
            Value = json
        }, ct);

        // (B) envia callback HTTP
        try
        {
            var res = await _http.PostAsJsonAsync(_callbackUrl, evt, ct);
            Console.WriteLine($"[CALLBACK] {status} -> {(int)res.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CALLBACK] falhou: {ex.Message}");
        }
    }

    private sealed record OutboundDto(
        string message_id, 
        string channel, 
        string to, 
        string? text, 
        string? file_id, 
        Guid conversation_id,
        Guid organizacao_id)
    {
        // JSON property names com underscore
        public string message_id { get; init; } = message_id;
        public string channel { get; init; } = channel;
        public string to { get; init; } = to;
        public string? text { get; init; } = text;
        public string? file_id { get; init; } = file_id;
        public Guid conversation_id { get; init; } = conversation_id;
        public Guid organizacao_id { get; init; } = organizacao_id;
    }
}
