using System;
using System.Text.Json;
using StackExchange.Redis;
using Chat.Server.Distributed;
using Chat.Shared.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Prometheus;
using Chat.Grpc;
using Chat.Server.Grpc;
using Microsoft.AspNetCore.Hosting;

namespace Chat.Server;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 5000;
        var nodeId = Environment.GetEnvironmentVariable("NODE_ID") ?? Environment.MachineName;
        var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "localhost:6379";
        var heartbeatSec = int.TryParse(Environment.GetEnvironmentVariable("HEARTBEAT_SEC"), out var hb) ? hb : 30;
        var idleTimeoutSec = int.TryParse(Environment.GetEnvironmentVariable("IDLE_TIMEOUT_SEC"), out var it) ? it : 90;
        var grpcPort = int.TryParse(Environment.GetEnvironmentVariable("GRPC_PORT"), out var gp) ? gp : 6000;

        Console.WriteLine($"[Server] NodeId={nodeId} Port={port} Redis={redisUrl}");

        // --- Redis / backplane
        var mux = await ConnectionMultiplexer.ConnectAsync(redisUrl);
        var presence = new RedisPresenceStore(mux);
        var groups = new RedisGroupStore(mux);
        var bus = new RedisBus(mux, nodeId);

        // --- Tabela de conexões (roteamento local + entre nós)
        var table = new ConnectionTable(
            nodeId: nodeId,
            presence: presence,
            groups: groups,
            bus: bus,
            presenceTtl: TimeSpan.FromSeconds(idleTimeoutSec)
        );

        var verbose = (Environment.GetEnvironmentVariable("DEMO_VERBOSE") ?? "true")
              .Equals("true", StringComparison.OrdinalIgnoreCase);

        if (verbose)
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    //Console.WriteLine("[Stats] " + Metrics.Snapshot(table));
                    await Task.Delay(5000);
                }
            });
        }

        // --- Assina o bus (mensagens roteadas por outros nós)
        bus.Subscribe(async json =>
        {
            try
            {
                var routed = Routed.Deserialize(json);
                if (routed is null || !string.Equals(routed.TargetNode, nodeId, StringComparison.OrdinalIgnoreCase)) return;
                if (verbose) Console.WriteLine($"[Bus][recv] from={routed.OriginNode} targets={(routed.Targets?.Length.ToString() ?? "-")}");

                var env = JsonSerializer.Deserialize<Envelope>(routed.EnvelopeJson);
                if (env is null) return;
                await table.DeliverFromBusAsync(env, routed.Targets);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bus] erro ao processar mensagem roteada: {ex.Message}");
            }
        });

        // === gRPC + Prometheus (INSERIR AQUI, antes do SocketServer) ===
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });

        builder.Services.AddGrpc();
        builder.Services.AddGrpcReflection();

        // injeta a mesma instância usada pelo servidor de sockets
        builder.Services.AddSingleton(table);
        builder.Services.AddSingleton(groups);
        builder.Services.AddSingleton(presence);
        builder.Services.AddSingleton(bus);
        builder.Services.AddSingleton<GrpcMetrics>();

        // var grpcPort = int.TryParse(Environment.GetEnvironmentVariable("GRPC_PORT"), out var gp) ? gp : 6000;
        var metricsPort = int.TryParse(Environment.GetEnvironmentVariable("METRICS_PORT"), out var mp) ? mp : 6060;

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(grpcPort, o =>
            {
                o.UseHttps();                         // <- TLS (usa dev cert no dev)
                o.Protocols = HttpProtocols.Http2;    // só HTTP/2 (recomendado)
                                                      // Se quiser compatibilidade com clientes HTTP/1.1, use:
                                                      // o.Protocols = HttpProtocols.Http1AndHttp2;
            });
        });

        var app = builder.Build();

        // métricas http (/metrics) + endpoints gRPC
        app.UseHttpMetrics();
        app.MapMetrics();
        app.MapGrpcService<Chat.Server.Grpc.ChatGrpcService>();
        app.MapGrpcReflectionService();
        app.MapGet("/", () => "Chat gRPC is up");

        _ = app.RunAsync();
        Console.WriteLine($"[gRPC] Escutando em 0.0.0.0:{grpcPort}");
        // === fim do bloco gRPC ===

        // --- Servidor de sockets (TCP) continua como estava
        var server = new SocketServer(port, table, heartbeatSec, idleTimeoutSec);
        await server.StartAsync();
    }
}
