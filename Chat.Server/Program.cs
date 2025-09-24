using System;
using System.Text.Json;
using StackExchange.Redis;
using Chat.Server.Distributed;
using Chat.Shared.Protocol;

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

        Console.WriteLine($"[Server] NodeId={nodeId} Port={port} Redis={redisUrl}");

        var mux = await ConnectionMultiplexer.ConnectAsync(redisUrl);
        var presence = new RedisPresenceStore(mux);
        var groups = new RedisGroupStore(mux);
        var bus = new RedisBus(mux, nodeId);

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
                    Console.WriteLine("[Stats] " + Metrics.Snapshot(table));
                    await Task.Delay(5000);
                }
            });
        }



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



        var server = new SocketServer(port, table, heartbeatSec, idleTimeoutSec);
        await server.StartAsync();
    }
}
