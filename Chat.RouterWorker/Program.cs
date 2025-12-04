using Chat.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chat.RouterWorker;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
            .ConfigureServices((ctx, services) =>
            {
                services.AddCassandraPersistence(ctx.Configuration);
                var kafkaBootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ??
                     Environment.GetEnvironmentVariable("WorkerKafka__BootstrapServers") ??
                     ctx.Configuration["WorkerKafka:BootstrapServers"] ??
                     "kafka:9092";

                services.Configure<WorkerKafkaOptions>(options =>
                {
                    options.BootstrapServers = kafkaBootstrap;
                    ctx.Configuration.GetSection("WorkerKafka").Bind(options);
                });
                services.AddHostedService<RouterWorkerService>();
            })
            .Build();

        await host.RunAsync();
    }
}
