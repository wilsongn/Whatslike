using Cassandra;
using Chat.Persistence.Abstractions;
using Chat.Persistence.Internal;
using Chat.Persistence.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Chat.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCassandraPersistence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CassandraOptions>(config.GetSection("Cassandra"));
        services.AddSingleton<CassandraSessionFactory>();
        services.AddSingleton(async sp =>
        {
            var f = sp.GetRequiredService<CassandraSessionFactory>();
            var s = await f.GetSessionAsync();
            await SchemaMigrator.MigrateAsync(s);
            return s;
        });
        services.AddSingleton<ISession>(sp => sp.GetRequiredService<Task<ISession>>().GetAwaiter().GetResult());
        services.AddSingleton<IMessageStore, CassandraMessageStore>();

        // opcional: executar migração no startup do host
        services.AddHostedService<StartupWarmup>();
        return services;
    }

    private sealed class StartupWarmup : IHostedService
    {
        private readonly Task<ISession> _sessionTask;
        public StartupWarmup(Task<ISession> sessionTask) => _sessionTask = sessionTask;
        public async Task StartAsync(CancellationToken cancellationToken) => await _sessionTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
