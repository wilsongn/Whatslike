using Chat.Persistence.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Chat.Persistence.Abstractions
{
    public interface IFileMetadataRepository
    {
        Task SaveAsync(FileMetadataRecord file, CancellationToken ct = default);
        Task<FileMetadataRecord?> GetByIdAsync(Guid arquivoId, CancellationToken ct = default);
    }
}
