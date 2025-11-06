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
                services.Configure<WorkerKafkaOptions>(ctx.Configuration.GetSection("Kafka"));
                services.AddHostedService<RouterWorkerService>();
            })
            .Build();

        await host.RunAsync();
    }
}
