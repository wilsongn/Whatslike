using Chat.Api.Infrastructure.Storage;
using Chat.Api.Messaging;
using Chat.Persistence;
using Chat.Persistence.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Minio;
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
            // sem expiração quando habilitado em config:
            ValidateLifetime = !disableLifetime,
            RequireExpirationTime = !disableLifetime,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // logar causa do 401 (ajuda a depurar)
        o.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                Console.WriteLine($"[JWT] Auth failed: {ctx.Exception.GetType().Name} - {ctx.Exception.Message}");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddKafkaProducer(builder.Configuration);
// Chat.Api/Program.cs
builder.Services.AddSingleton<IMessagePublisher>(_ =>
    new KafkaMessagePublisher(Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP") ?? "localhost:9092"));



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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
