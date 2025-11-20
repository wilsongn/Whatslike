using Cassandra;
using Chat.Persistence.Abstractions;
using Chat.Persistence.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Chat.Persistence
{
    public sealed class CassandraFileMetadataRepository : IFileMetadataRepository
    {
        private readonly ISession _s;

        public CassandraFileMetadataRepository(ISession session)
        {
            _s = session;
        }

        public async Task SaveAsync(FileMetadataRecord f, CancellationToken ct = default)
        {
            var stmt = new SimpleStatement(
                @"INSERT INTO arquivos (
                  arquivo_id,
                  conversa_id,
                  usuario_remetente_id,
                  bucket,
                  object_key,
                  tamanho_bytes,
                  content_type,
                  checksum_sha256,
                  criado_em
              ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                f.ArquivoId,
                f.ConversaId,
                f.UsuarioRemetenteId,
                f.Bucket,
                f.ObjectKey,
                f.TamanhoBytes,
                f.ContentType,
                f.ChecksumSha256,
                f.CriadoEm.UtcDateTime
            );

            await _s.ExecuteAsync(stmt).ConfigureAwait(false);
        }

        public async Task<FileMetadataRecord?> GetByIdAsync(Guid arquivoId, CancellationToken ct = default)
        {
            var stmt = new SimpleStatement(
                @"SELECT arquivo_id,
                     conversa_id,
                     usuario_remetente_id,
                     bucket,
                     object_key,
                     tamanho_bytes,
                     content_type,
                     checksum_sha256,
                     criado_em
              FROM arquivos
              WHERE arquivo_id = ?",
                arquivoId
            );

            var rs = await _s.ExecuteAsync(stmt).ConfigureAwait(false);
            var row = rs.FirstOrDefault();
            if (row == null)
                return null;

            return new FileMetadataRecord
            {
                ArquivoId = row.GetValue<Guid>("arquivo_id"),
                ConversaId = row.GetValue<Guid>("conversa_id"),
                UsuarioRemetenteId = row.GetValue<Guid>("usuario_remetente_id"),
                Bucket = row.GetValue<string>("bucket"),
                ObjectKey = row.GetValue<string>("object_key"),
                TamanhoBytes = row.GetValue<long>("tamanho_bytes"),
                ContentType = row.GetValue<string>("content_type"),
                ChecksumSha256 = row.GetValue<string>("checksum_sha256"),
                CriadoEm = new DateTimeOffset(row.GetValue<DateTime>("criado_em"), TimeSpan.Zero)
            };
        }
    }
}
