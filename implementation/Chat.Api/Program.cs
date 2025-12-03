using Chat.Api.Infrastructure.Storage;
using Chat.Api.Messaging;
using Chat.Api.WebSockets;
using Chat.Persistence;
using Chat.Persistence.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Minio;
using StackExchange.Redis;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// JWT
var jwt = builder.Configuration.GetSection("Jwt");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));
var disableLifetime = builder.Configuration.GetValue("Jwt:DisableLifetimeValidation", false);

builder.Services
    .AddAuthentication(o =>
    {
        o.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = false; // DEV
        o.SaveToken = true;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = !disableLifetime,
            RequireExpirationTime = !disableLifetime,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // Suporte a token via query string para WebSocket
        o.Events = new JwtBearerEvents
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
            },
            OnAuthenticationFailed = ctx =>
            {
                Console.WriteLine($"[JWT] Auth failed: {ctx.Exception.GetType().Name} - {ctx.Exception.Message}");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddKafkaProducer(builder.Configuration);

builder.Services.AddSingleton<IMessagePublisher>(_ =>
    new KafkaMessagePublisher(Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "localhost:9092"));

// Redis para WebSocket notifications
var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "localhost:6379";
var redis = await ConnectionMultiplexer.ConnectAsync(redisUrl);
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

// WebSocket Hub
builder.Services.AddSingleton<WebSocketHub>();

builder.Services.AddCassandraPersistence(builder.Configuration);
builder.Services.AddControllers();

// Swagger + Bearer
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Chat API", Version = "v1" });

    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Informe: Bearer {seu token}"
    };
    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [securityScheme] = Array.Empty<string>()
    });
});

// MinIO
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var section = configuration.GetSection("Minio");

    return new MinioClient()
        .WithEndpoint(section["Endpoint"])
        .WithCredentials(section["AccessKey"], section["SecretKey"])
        .WithSSL(bool.Parse(section["UseSSL"] ?? "false"))
        .Build();
});

builder.Services.AddScoped<IObjectStorageService, MinioObjectStorageService>();
builder.Services.AddSingleton<IFileMetadataRepository, CassandraFileMetadataRepository>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// ===== IMPORTANTE: WebSocket deve vir ANTES de UseAuthentication =====
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseAuthentication();
app.UseAuthorization();

// Middleware customizado para WebSocket (após Authentication)
app.UseWebSocketHub();

app.MapControllers();

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

Console.WriteLine($"[Chat.Api] Redis={redisUrl}");
Console.WriteLine("[Chat.Api] WebSocket endpoint: /ws/status?access_token={JWT}");

app.Run();
