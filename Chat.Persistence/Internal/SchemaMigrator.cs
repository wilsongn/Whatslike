using Cassandra;

namespace Chat.Persistence.Internal;

internal static class SchemaMigrator
{
    public static async Task MigrateAsync(ISession s)
    {
        // Tabelas principais
        var cql = new[]
        {
            // mensagens
            @"CREATE TABLE IF NOT EXISTS mensagens (
                organizacao_id uuid,
                conversa_id uuid,
                bucket int,
                sequencia bigint,
                mensagem_id uuid,
                direcao text,
                usuario_remetente_id uuid,
                conteudo text,
                status text,
                status_ts map<text, timestamp>,
                id_msg_provedor text,
                criado_em timestamp,
                PRIMARY KEY ((organizacao_id, conversa_id, bucket), sequencia)
            ) WITH CLUSTERING ORDER BY (sequencia ASC)
              AND compaction = {'class': 'TimeWindowCompactionStrategy', 'compaction_window_unit':'DAYS','compaction_window_size':'1'};",

            // idempotência (chave de negócio)
            @"CREATE TABLE IF NOT EXISTS idempotencia_mensagem (
                organizacao_id uuid,
                chave text,
                mensagem_id uuid,
                criado_em timestamp,
                PRIMARY KEY ((organizacao_id), chave)
            );",

            // entregas
            @"CREATE TABLE IF NOT EXISTS entregas (
                organizacao_id uuid,
                mensagem_id uuid,
                conector_id uuid,
                tentativa int,
                status text,
                proxima_tentativa_em timestamp,
                id_msg_provedor text,
                codigo_erro text,
                mensagem_erro text,
                debug text,
                criado_em timestamp,
                PRIMARY KEY ((organizacao_id, mensagem_id, conector_id), tentativa)
            ) WITH compaction = {'class': 'TimeWindowCompactionStrategy', 'compaction_window_unit':'HOURS','compaction_window_size':'1'};",

            // alocador de sequência por conversa/bucket (LWT)
            @"CREATE TABLE IF NOT EXISTS sequencia_conversa (
                organizacao_id uuid,
                conversa_id uuid,
                bucket int,
                proxima_sequencia bigint,
                PRIMARY KEY ((organizacao_id, conversa_id, bucket))
            );"
        };

        foreach (var stmt in cql)
            await s.ExecuteAsync(new SimpleStatement(stmt));
    }
}
