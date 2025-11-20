using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Chat.Persistence.Models
{
    public sealed class FileMetadataRecord
    {
        public Guid ArquivoId { get; set; }
        public Guid ConversaId { get; set; }
        public Guid UsuarioRemetenteId { get; set; }

        public string Bucket { get; set; } = "";
        public string ObjectKey { get; set; } = "";

        public long TamanhoBytes { get; set; }
        public string ContentType { get; set; } = "";
        public string ChecksumSha256 { get; set; } = "";

        public DateTimeOffset CriadoEm { get; set; }
    }

}
