using System.Text.Json;
using Confluent.Kafka;
using Chat.Persistence.Abstractions;
using Chat.Persistence.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Confluent.Kafka.Admin;

namespace Chat.RouterWorker;

public sealed class RouterWorkerService : BackgroundService
{
    private readonly ILogger<RouterWorkerService> _log;
    private readonly IMessageStore _store;
    private readonly WorkerKafkaOptions _opt;

    public RouterWorkerService(ILogger<RouterWorkerService> log, IMessageStore store, IOptions<WorkerKafkaOptions> opt)
    { _log = log; _store = store; _opt = opt.Value; }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("RouterWorker starting. topic={Topic} group={Group}", _opt.TopicMessages, _opt.GroupId);

        var adminConf = new AdminClientConfig { BootstrapServers = _opt.BootstrapServers };
        using var admin = new AdminClientBuilder(adminConf).Build();

        try
        {
            // Verifica se o tópico já existe
            var meta = admin.GetMetadata(_opt.TopicMessages, TimeSpan.FromSeconds(3));
            var exists = meta.Topics.Any(t => t.Topic == _opt.TopicMessages && t.Error.Code == ErrorCode.NoError);

            if (!exists)
            {
                await admin.CreateTopicsAsync(new[]
                {
                new TopicSpecification
                {
                    Name = _opt.TopicMessages,
                    NumPartitions = _opt.Partitions,
                    ReplicationFactor = _opt.ReplicationFactor
                }
            },
                new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(10) });

                _log.LogInformation("Tópico '{Topic}' criado com {P} partições.", _opt.TopicMessages, _opt.Partitions);
            }
            else
            {
                _log.LogInformation("Tópico '{Topic}' já existe.", _opt.TopicMessages);
            }
        }
        catch (CreateTopicsException ex) when (ex.Results.Any(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
        {
            _log.LogInformation("Tópico '{Topic}' já existia (race).", _opt.TopicMessages);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao garantir tópico '{Topic}'. Continuando…", _opt.TopicMessages);
        }

        await base.StartAsync(cancellationToken);
    }


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var conf = new ConsumerConfig
        {
            BootstrapServers = _opt.BootstrapServers,
            GroupId = _opt.GroupId,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<string, string>(conf).Build();
        consumer.Subscribe(_opt.TopicMessages);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var cr = consumer.Consume(stoppingToken);
                var val = cr.Message.Value;

                MessageProducedEvent? evt = JsonSerializer.Deserialize<MessageProducedEvent>(val);
                if (evt == null)
                {
                    _log.LogWarning("Mensagem inválida no Kafka offset {Offset}", cr.Offset);
                    consumer.Commit(cr);
                    continue;
                }

                // persist
                var rec = new MessageRecord
                {
                    OrganizacaoId = evt.OrganizacaoId,
                    ConversaId = evt.ConversaId,
                    MensagemId = evt.MensagemId,
                    UsuarioRemetenteId = evt.UsuarioRemetenteId,
                    Direcao = evt.Direcao,
                    ConteudoJson = evt.ConteudoJson,
                    Status = "sent",
                    CriadoEm = evt.CriadoEm
                };

                var seq = await _store.InsertMessageAsync(rec);
                var bucket = _store.ComputeBucket(evt.CriadoEm);
                await _store.UpdateMessageStatusAsync(evt.OrganizacaoId, evt.ConversaId, bucket, seq, "delivered");

                _log.LogInformation("Persistido conversa={ConversaId} seq={Seq} offset={Offset}", evt.ConversaId, seq, cr.Offset);

                consumer.Commit(cr);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            consumer.Close();
        }
    }

    private sealed record MessageProducedEvent(
        Guid OrganizacaoId,
        Guid ConversaId,
        Guid MensagemId,
        Guid UsuarioRemetenteId,
        string Direcao,
        string ConteudoJson,
        DateTimeOffset CriadoEm
    );
}
