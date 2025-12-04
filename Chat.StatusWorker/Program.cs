using Chat.StatusWorker;
using Chat.Persistence;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Configurações
var kafkaBootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ??
                     Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ??
                     "kafka:9092";
var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "localhost:6379";
var topicStatus = Environment.GetEnvironmentVariable("TOPIC_STATUS") ?? "msg.status";
var groupId = Environment.GetEnvironmentVariable("GROUP_ID") ?? "status-worker";

Console.WriteLine($"[StatusWorker] Kafka={kafkaBootstrap} Redis={redisUrl} Topic={topicStatus}");

// Redis para notificações WebSocket
var redis = await ConnectionMultiplexer.ConnectAsync(redisUrl);
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

// Cassandra para persistência
builder.Services.AddCassandraPersistence(builder.Configuration);

// Worker
builder.Services.AddSingleton(new StatusWorkerOptions
{
    KafkaBootstrap = kafkaBootstrap,
    TopicStatus = topicStatus,
    GroupId = groupId
});

builder.Services.AddHostedService<StatusWorkerService>();

var host = builder.Build();
await host.RunAsync();
