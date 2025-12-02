namespace Chat.Api.Controllers
{
    using Chat.Api.Contracts;
    using Chat.Api.Infrastructure.Storage;
    using Microsoft.AspNetCore.Mvc;
    using Chat.Persistence.Models;
    using Chat.Persistence.Abstractions;

    [ApiController]
    [Route("v1/files")]
    public class FilesController : ControllerBase
    {
        private readonly IObjectStorageService _storage;
        private readonly IFileMetadataRepository _files;
        private readonly IConfiguration _config;

        public FilesController(
            IObjectStorageService storage,
            IFileMetadataRepository files,
            IConfiguration config)
        {
            _storage = storage;
            _files = files;
            _config = config;
        }

        [HttpPost]
        [RequestSizeLimit(2L * 1024 * 1024 * 1024)]
        public async Task<ActionResult<UploadFileResponse>> Upload(
            [FromForm] IFormFile file,
            [FromForm] Guid conversationId,
            [FromForm] Guid usuarioRemetenteId,
            CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                return BadRequest("Arquivo vazio.");

            await using var stream = file.OpenReadStream();

            var stored = await _storage.UploadAsync(
                stream,
                file.FileName,
                file.ContentType ?? "application/octet-stream",
                conversationId,
                uploaderId: usuarioRemetenteId,
                ct);

            var meta = new FileMetadataRecord
            {
                ArquivoId = stored.FileId,
                ConversaId = conversationId,
                UsuarioRemetenteId = usuarioRemetenteId,
                Bucket = stored.Bucket,
                ObjectKey = stored.ObjectKey,
                TamanhoBytes = stored.Size,
                ContentType = stored.ContentType,
                ChecksumSha256 = stored.ChecksumSha256,
                CriadoEm = DateTimeOffset.UtcNow
            };

            await _files.SaveAsync(meta, ct);

            var response = new UploadFileResponse
            {
                FileId = meta.ArquivoId,
                ConversationId = meta.UsuarioRemetenteId,
                Size = meta.TamanhoBytes,
                ContentType = meta.ContentType,
                ChecksumSha256 = meta.ChecksumSha256
            };

            return Ok(response);
        }

        [HttpGet("{fileId}/url")]
        public async Task<ActionResult<object>> GetDownloadUrl(
            [FromRoute] Guid fileId,
            CancellationToken ct)
        {
            var meta = await _files.GetByIdAsync(fileId, ct);
            if (meta == null)
                return NotFound();

            var expirySeconds = int.Parse(
                _config["Minio:PresignedUrlExpirySeconds"] ?? "3600");

            var url = await _storage.GetDownloadUrlAsync(
                meta.Bucket,
                meta.ObjectKey,
                TimeSpan.FromSeconds(expirySeconds),
                ct);

            return Ok(new { url, expiresIn = expirySeconds });
        }
    }

}
