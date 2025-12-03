using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

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

        // Permitir token via query string (para WebSocket)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ============================================
// YARP Reverse Proxy
// ============================================
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// ============================================
// CORS
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
app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// Reverse Proxy (último middleware)
app.MapReverseProxy();

// ============================================
// Endpoints diretos (não proxied)
// ============================================

// Health Check
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Chat.ApiGateway",
    timestamp = DateTimeOffset.UtcNow
}))
.AllowAnonymous()
.WithName("HealthCheck");

// Root
app.MapGet("/", () => Results.Ok(new
{
    service = "Chat API Gateway",
    version = "1.0.0",
    endpoints = new[]
    {
        "POST /api/v1/messages - Send message (proxied to Frontend)",
        "WS   /ws - WebSocket connection (proxied to Notification)",
        "GET  /health - Health check"
    },
    documentation = "Rate limiting: Built into YARP (configure in appsettings.json)"
}))
.AllowAnonymous()
.WithName("Root");

// ============================================
// Logging de startup
// ============================================
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("===========================================");
logger.LogInformation("Chat.ApiGateway starting...");
logger.LogInformation("Environment: {Env}", app.Environment.EnvironmentName);
logger.LogInformation("===========================================");

// Log das rotas configuradas
var proxyConfig = builder.Configuration.GetSection("ReverseProxy");
var routes = proxyConfig.GetSection("Routes").GetChildren();

logger.LogInformation("Configured Routes:");
foreach (var route in routes)
{
    var routeId = route.Key;
    var path = route.GetValue<string>("Match:Path");
    var clusterId = route.GetValue<string>("ClusterId");
    logger.LogInformation("  - {RouteId}: {Path} -> {ClusterId}", routeId, path, clusterId);
}

app.Run();