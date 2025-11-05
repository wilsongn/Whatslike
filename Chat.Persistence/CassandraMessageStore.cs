using Cassandra;
using Chat.Persistence.Abstractions;
using Chat.Persistence.Models;
using Chat.Persistence.Options;
using Microsoft.Extensions.Options;

namespace Chat.Persistence;

public sealed class CassandraMessageStore : IMessageStore
{
    private readonly ISession _s;
    private readonly CassandraOptions _opt;

    public CassandraMessageStore(ISession session, IOptions<CassandraOptions> opt)
    {
        _s = session;
        _opt = opt.Value;
    }

    public int ComputeBucket(DateTimeOffset utcCreatedAt)
    {
        return _opt.BucketStrategy.Equals("SeqBlock", StringComparison.OrdinalIgnoreCase)
            ? 0
            : utcCreatedAt.UtcDateTime.Year * 100 + utcCreatedAt.UtcDateTime.Month; // yyyymm
    }

    public async Task<long> InsertMessageAsync(MessageRecord m, CancellationToken ct = default)
    {
        m.Bucket = ComputeBucket(m.CriadoEm);

        var allocator = new Internal.SequenceAllocator(_s);
        m.Sequencia = await allocator.NextAsync(m.OrganizacaoId, m.ConversaId, m.Bucket, ct);

        var stmt = new SimpleStatement(
            @"INSERT INTO mensagens (organizacao_id, conversa_id, bucket, sequencia, mensagem_id, direcao, usuario_remetente_id, conteudo, status, status_ts, id_msg_provedor, criado_em)
              VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            m.OrganizacaoId, m.ConversaId, m.Bucket, m.Sequencia, m.MensagemId, m.Direcao, m.UsuarioRemetenteId,
            m.ConteudoJson, m.Status,
            new Dictionary<string, DateTime> { { m.Status, m.CriadoEm.UtcDateTime } },
            null, m.CriadoEm.UtcDateTime
        );

        await _s.ExecuteAsync(stmt).ConfigureAwait(false);
        return m.Sequencia;
    }

    public async Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
        Guid organizacaoId, Guid conversaId, int bucket, long? fromSeq, int limit, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("SELECT organizacao_id, conversa_id, bucket, sequencia, mensagem_id, direcao, usuario_remetente_id, conteudo, status, criado_em FROM mensagens WHERE organizacao_id=? AND conversa_id=? AND bucket=?");
        var args = new List<object> { organizacaoId, conversaId, bucket };

        if (fromSeq.HasValue)
        {
            sb.Append(" AND sequencia >= ?");
            args.Add(fromSeq.Value);
        }
        sb.Append(" LIMIT ?");
        args.Add(limit);

        var stmt = new SimpleStatement(sb.ToString(), args.ToArray());
        var rs = await _s.ExecuteAsync(stmt).ConfigureAwait(false);

        var list = new List<MessageRecord>();
        foreach (var row in rs)
        {
            list.Add(new MessageRecord
            {
                OrganizacaoId = row.GetValue<Guid>("organizacao_id"),
                ConversaId = row.GetValue<Guid>("conversa_id"),
                Bucket = row.GetValue<int>("bucket"),
                Sequencia = row.GetValue<long>("sequencia"),
                MensagemId = row.GetValue<Guid>("mensagem_id"),
                Direcao = row.GetValue<string>("direcao"),
                UsuarioRemetenteId = row.GetValue<Guid>("usuario_remetente_id"),
                ConteudoJson = row.GetValue<string>("conteudo"),
                Status = row.GetValue<string>("status"),
                CriadoEm = new DateTimeOffset(row.GetValue<DateTime>("criado_em"), TimeSpan.Zero)
            });
        }
        return list;
    }
}
