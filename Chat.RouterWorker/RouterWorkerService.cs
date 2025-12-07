using Cassandra;
using Chat.Persistence;
using Chat.Persistence.Abstractions;
using Chat.Persistence.Models;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Chat.RouterWorker;

public sealed class RouterWorkerService : BackgroundService
{
    private readonly ILogger<RouterWorkerService> _log;
    private readonly IMessageStore _store;
    private readonly WorkerKafkaOptions _opt;
    private IProducer<string, string>? _producer;

    public RouterWorkerService(ILogger<RouterWorkerService> log, IMessageStore store, IOptions<WorkerKafkaOptions> opt)
    {
        _log = log;
        _store = store;
        _opt = opt.Value;
    }

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

        // Aguardar metadata do Kafka atualizar
        _log.LogInformation("Aguardando 5s para metadata do Kafka atualizar...");
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        // Inicializar Producer
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _opt.BootstrapServers,
            EnableIdempotence = true
        };
        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
        _log.LogInformation("Kafka Producer inicializado para RouterWorker");

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

        _log.LogInformation("RouterWorker consumindo de '{Topic}'", _opt.TopicMessages);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var cr = consumer.Consume(stoppingToken);
                var val = cr.Message.Value;

                MessageProducedEvent? evt = JsonSerializer.Deserialize<MessageProducedEvent>(val, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (evt == null)
                {
                    _log.LogWarning("Mensagem null ou inválida em offset {Offset}", cr.Offset);
                    consumer.Commit(cr);
                    continue;
                }

                if (evt.Direcao == "read-receipt")
                {
                    _log.LogInformation("[READ] Usuário {User} leu conversa {Conv}", evt.UsuarioRemetenteId, evt.ConversaId);
                    consumer.Commit(cr);
                    continue;
                }

                // Persistir no Cassandra
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

                _log.LogInformation("Persistido conversa={ConversaId} seq={Seq} offset={Offset} canal={Canal}",
                    evt.ConversaId, seq, cr.Offset, evt.Canal);

                // ========== Publicar no tópico do canal (whatsapp ou instagram) ==========
                var channel = evt.Canal?.ToLowerInvariant() ?? "whatsapp";
                var outTopic = channel switch
                {
                    "instagram" => "msg.out.instagram",
                    "whatsapp" => "msg.out.whatsapp",
                    _ => "msg.out.whatsapp"
                };

                var outEvent = new
                {
                    messageId = evt.MensagemId.ToString(),
                    conversationId = evt.ConversaId.ToString(),
                    organizationId = evt.OrganizacaoId.ToString(),
                    senderId = evt.UsuarioRemetenteId.ToString(),
                    content = evt.ConteudoJson,
                    timestamp = evt.CriadoEm.ToUnixTimeMilliseconds(),
                    channel = channel
                };

                var outJson = JsonSerializer.Serialize(outEvent);

                try
                {
                    var deliveryResult = await _producer!.ProduceAsync(
                        outTopic,
                        new Message<string, string>
                        {
                            Key = evt.ConversaId.ToString(),
                            Value = outJson
                        });

                    _log.LogInformation(
                        "Publicado em {Topic}: MessageId={MessageId} Canal={Channel} Partition={Partition} Offset={Offset}",
                        outTopic, evt.MensagemId, channel, deliveryResult.Partition.Value, deliveryResult.Offset.Value);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Erro ao publicar em {Topic}: MessageId={MessageId}", outTopic, evt.MensagemId);
                }

                consumer.Commit(cr);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("RouterWorker cancelado");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erro fatal no RouterWorker");
            throw;
        }
        finally
        {
            consumer.Close();
            _producer?.Dispose();
        }
    }

    private sealed class MessageProducedEvent
    {
        public Guid ConversaId { get; set; }
        public Guid MensagemId { get; set; }
        public Guid UsuarioRemetenteId { get; set; }
        public Guid OrganizacaoId { get; set; }
        public string Direcao { get; set; } = string.Empty;
        public string Canal { get; set; } = "whatsapp";
        public string ConteudoJson { get; set; } = string.Empty;
        public DateTimeOffset CriadoEm { get; set; }
    }
}