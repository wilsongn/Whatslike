// Chat.Persistence/Internal/SequenceAllocator.cs
using Cassandra;

namespace Chat.Persistence.Internal;

internal sealed class SequenceAllocator
{
    private readonly ISession _s;

    // prepared statements
    private readonly PreparedStatement _psInsertIfNotExists;
    private readonly PreparedStatement _psSelect;
    private readonly PreparedStatement _psUpdateCas;

    public SequenceAllocator(ISession session)
    {
        _s = session;

        // PREPARE garante que a CQL vá certinha ao servidor
        _psInsertIfNotExists = _s.Prepare(
            "INSERT INTO sequencia_conversa (organizacao_id, conversa_id, bucket, proxima_sequencia) " +
            "VALUES (?, ?, ?, ?) IF NOT EXISTS");

        _psSelect = _s.Prepare(
            "SELECT proxima_sequencia FROM sequencia_conversa " +
            "WHERE organizacao_id=? AND conversa_id=? AND bucket=?");

        _psUpdateCas = _s.Prepare(
            "UPDATE sequencia_conversa SET proxima_sequencia=? " +
            "WHERE organizacao_id=? AND conversa_id=? AND bucket=? IF proxima_sequencia=?");
    }

    public async Task<long> NextAsync(Guid org, Guid conv, int bucket, CancellationToken ct = default)
    {
        // 1) tenta criar o registro com 1 via LWT
        var rsIns = await _s.ExecuteAsync(_psInsertIfNotExists.Bind(org, conv, bucket, 1L))
                            .ConfigureAwait(false);
        var applied = rsIns.FirstOrDefault()?.GetValue<bool>("[applied]") ?? false;
        if (applied) return 1L;

        // 2) CAS loop: lê valor corrente e tenta incrementar
        for (int i = 0; i < 16; i++)
        {
            var row = (await _s.ExecuteAsync(_psSelect.Bind(org, conv, bucket)).ConfigureAwait(false))
                        .FirstOrDefault();
            var current = row?.GetValue<long>("proxima_sequencia") ?? 0L;
            var next = current + 1;

            var rs = await _s.ExecuteAsync(_psUpdateCas.Bind(next, org, conv, bucket, current))
                              .ConfigureAwait(false);
            var ok = rs.FirstOrDefault()?.GetValue<bool>("[applied]") ?? false;
            if (ok) return next;
        }

        throw new InvalidOperationException("Falha ao alocar sequência (muitos conflitos).");
    }
}
