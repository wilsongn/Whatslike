using Confluent.Kafka;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Chat.Frontend.Services;
using Chat.Frontend.WebSockets;

var builder = WebApplication.CreateBuilder(args);

// ============================================
// JWT Authentication
// ============================================
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var issuer = builder.Configuration["JWT_ISSUER"] ??
                    Environment.GetEnvironmentVariable("JWT_ISSUER") ??
                    "chat-dev";

        var audience = builder.Configuration["JWT_AUDIENCE"] ??
                      Environment.GetEnvironmentVariable("JWT_AUDIENCE") ??
                      "chat-api";

        var secret = builder.Configuration["JWT_SECRET"] ??
                    Environment.GetEnvironmentVariable("JWT_SECRET") ??
                    "26c8d9a793975af4999bc048990f6fd1";

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,

            ValidateAudience = true,
            ValidAudience = audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        // Suporte a token via query string para WebSocket
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                // Permitir token via query string para WebSocket
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/ws"))
                {
                    ctx.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ============================================
// Controllers
// ============================================
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ============================================
// Kafka Producer
// ============================================
builder.Services.AddSingleton<IProducer<string, string>>(sp =>
{
    var kafkaServers = builder.Configuration["Kafka:BootstrapServers"] ??
                       Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ??
                       "localhost:9092";

    var config = new ProducerConfig
    {
        BootstrapServers = kafkaServers,
        Acks = Acks.All,                    // Garantia de entrega
        EnableIdempotence = true,           // Idempotência no Kafka
        MaxInFlight = 1,                    // Ordem garantida por partição
        MessageSendMaxRetries = 3,
        CompressionType = CompressionType.Snappy,
        LingerMs = 10                       // Batch de mensagens
    };

    var producer = new ProducerBuilder<string, string>(config)
        .SetErrorHandler((_, e) =>
        {
            var logger = sp.GetRequiredService<ILogger<Program>>();
            logger.LogError("Kafka Producer Error: {Reason}", e.Reason);
        })
        .SetLogHandler((_, log) =>
        {
            var logger = sp.GetRequiredService<ILogger<Program>>();
            if (log.Level <= SyslogLevel.Warning)
            {
                logger.LogWarning("Kafka: {Message}", log.Message);
            }
        })
        .Build();

    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Kafka Producer initialized with servers: {Servers}", kafkaServers);

    return producer;
});

// ============================================
// Redis Cache (para idempotência)
// ============================================
builder.Services.AddStackExchangeRedisCache(options =>
{
    var redisConnection = builder.Configuration["Redis:ConnectionString"] ??
                          Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ??
                          "localhost:6379";

    options.Configuration = redisConnection;
    options.InstanceName = "ChatFrontend:";

    var logger = builder.Logging.Services.BuildServiceProvider()
        .GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Redis Cache configured: {Connection}", redisConnection);
});

// ============================================
// Application Services
// ============================================
builder.Services.AddSingleton<IdempotencyService>();

// ============================================
// CORS (se necessário para frontend web)
// ============================================
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// ============================================
// Middleware Pipeline
// ============================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// ===== WebSocket DEVE vir ANTES de Authentication =====
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseAuthentication();
app.UseAuthorization();

// ===== WebSocket Proxy Middleware (após Authentication) =====
app.UseWebSocketProxy();

app.MapControllers();

// ============================================
// Health Check Endpoint
// ============================================
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Chat.Frontend",
    timestamp = DateTimeOffset.UtcNow
}))
.AllowAnonymous();

// ============================================
// Root Endpoint
// ============================================
app.MapGet("/", () => Results.Ok(new
{
    service = "Chat Frontend API",
    version = "1.0.0",
    endpoints = new[]
    {
        "POST /api/v1/messages - Send message",
        "GET  /api/v1/messages/{conversationId} - Get conversation (TODO)",
        "WS   /ws/status?access_token={JWT} - WebSocket status notifications",
        "GET  /health - Health check"
    }
}))
.AllowAnonymous();

// ============================================
// Logging de startup
// ============================================
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var chatApiUrl = Environment.GetEnvironmentVariable("CHAT_API_URL") ??
                builder.Configuration["ChatApi:BaseUrl"] ??
                "http://localhost:5000";

logger.LogInformation("===========================================");
logger.LogInformation("Chat.Frontend starting...");
logger.LogInformation("Environment: {Env}", app.Environment.EnvironmentName);
logger.LogInformation("Chat.Api Backend: {ChatApiUrl}", chatApiUrl);
logger.LogInformation("WebSocket Proxy: /ws/status -> {ChatApiUrl}/ws/status", chatApiUrl);
logger.LogInformation("===========================================");

app.Run();
