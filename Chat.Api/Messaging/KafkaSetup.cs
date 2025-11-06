using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Chat.Api.Messaging;

public static class KafkaSetup
{
    public static IServiceCollection AddKafkaProducer(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<KafkaOptions>(cfg.GetSection("Kafka"));
        services.AddSingleton<IProducer<string, string>>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
            var conf = new ProducerConfig
            {
                BootstrapServers = opt.BootstrapServers,
                ClientId = opt.ClientId,
                Acks = Acks.All,
                EnableIdempotence = true
            };
            return new ProducerBuilder<string, string>(conf).Build();
        });
        return services;
    }
}
