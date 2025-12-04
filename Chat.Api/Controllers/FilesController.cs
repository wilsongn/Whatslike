// Chat.Api/Controllers/FilesController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;
using System.Security.Claims;
using System.Security.Cryptography;

namespace Chat.Api.Controllers;

[ApiController]
[Route("api/v1/files")]
[Authorize]
public class FilesController : ControllerBase
{
    private readonly IMinioClient _minioClient;
    private readonly ILogger<FilesController> _logger;
    private readonly string _bucketName;
    private readonly int _presignedUrlExpiry;

    public FilesController(
        IMinioClient minioClient,
        IConfiguration configuration,
        ILogger<FilesController> logger)
    {
        _minioClient = minioClient;
        _logger = logger;
        _bucketName = configuration["Minio:BucketName"] ?? "whatslike-files";
        _presignedUrlExpiry = int.Parse(configuration["Minio:PresignedUrlExpirySeconds"] ?? "3600");
    }

    /// <summary>
    /// Upload de arquivo (até 2GB)
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(2_147_483_648)] // 2GB
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
    public async Task<IActionResult> UploadFile(
        IFormFile file,
        [FromForm] string conversationId,
        [FromForm] string? description)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "File is required" });
        }

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
        var organizationId = User.FindFirst("tenant_id")?.Value ?? Guid.Empty.ToString();

        var fileId = Guid.NewGuid().ToString();
        var extension = Path.GetExtension(file.FileName);
        var objectName = $"{organizationId}/{conversationId}/{fileId}{extension}";

        _logger.LogInformation(
            "Uploading file: FileId={FileId}, FileName={FileName}, Size={Size}, ConversationId={ConversationId}",
            fileId, file.FileName, file.Length, conversationId);

        try
        {
            // Garantir que bucket existe
            var bucketExists = await _minioClient.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(_bucketName));

            if (!bucketExists)
            {
                await _minioClient.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(_bucketName));
                _logger.LogInformation("Bucket '{Bucket}' criado", _bucketName);
            }

            // Calcular checksum (SHA256)
            string checksum;
            using (var sha256 = SHA256.Create())
            {
                using var stream = file.OpenReadStream();
                var hash = await sha256.ComputeHashAsync(stream);
                checksum = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }

            // Upload para MinIO
            using var fileStream = file.OpenReadStream();
            await _minioClient.PutObjectAsync(new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithStreamData(fileStream)
                .WithObjectSize(file.Length)
                .WithContentType(file.ContentType));

            _logger.LogInformation(
                "File uploaded successfully: FileId={FileId}, ObjectName={ObjectName}, Checksum={Checksum}",
                fileId, objectName, checksum);

            // TODO: Salvar metadata no Cassandra
            // - fileId, fileName, size, contentType, checksum
            // - uploaderId, conversationId, organizationId
            // - uploadedAt

            return Ok(new
            {
                fileId,
                fileName = file.FileName,
                size = file.Length,
                contentType = file.ContentType,
                checksum,
                conversationId,
                uploadedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file: FileId={FileId}", fileId);
            return StatusCode(500, new { error = "Failed to upload file" });
        }
    }

    /// <summary>
    /// Obter URL temporária para download
    /// </summary>
    [HttpGet("{fileId}/download-url")]
    public async Task<IActionResult> GetDownloadUrl(string fileId)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var organizationId = User.FindFirst("tenant_id")?.Value ?? Guid.Empty.ToString();

        _logger.LogInformation(
            "Generating download URL: FileId={FileId}, UserId={UserId}",
            fileId, userId);

        try
        {
            // TODO: Buscar metadata no Cassandra para pegar o objectName correto
            // Por enquanto, assumir padrão: {orgId}/{convId}/{fileId}.ext
            // Em produção, fazer SELECT no Cassandra

            // Exemplo temporário (ajustar com dados reais do Cassandra):
            var objectName = $"{organizationId}/*/{fileId}.*"; // Buscar padrão

            // Gerar presigned URL válida por X segundos
            var presignedUrl = await _minioClient.PresignedGetObjectAsync(
                new PresignedGetObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectName)
                    .WithExpiry(_presignedUrlExpiry));

            _logger.LogInformation(
                "Download URL generated: FileId={FileId}, ExpiresIn={Expiry}s",
                fileId, _presignedUrlExpiry);

            return Ok(new
            {
                fileId,
                downloadUrl = presignedUrl,
                expiresIn = _presignedUrlExpiry,
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(_presignedUrlExpiry)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating download URL: FileId={FileId}", fileId);
            return StatusCode(500, new { error = "Failed to generate download URL" });
        }
    }

    /// <summary>
    /// Listar arquivos de uma conversa
    /// </summary>
    [HttpGet("conversation/{conversationId}")]
    public async Task<IActionResult> ListConversationFiles(string conversationId)
    {
        var organizationId = User.FindFirst("tenant_id")?.Value ?? Guid.Empty.ToString();
        var prefix = $"{organizationId}/{conversationId}/";

        _logger.LogInformation(
            "Listing files: ConversationId={ConversationId}, Prefix={Prefix}",
            conversationId, prefix);

        try
        {
            var files = new List<object>();

            var listArgs = new ListObjectsArgs()
                .WithBucket(_bucketName)
                .WithPrefix(prefix)
                .WithRecursive(true);

            await foreach (var item in _minioClient.ListObjectsEnumAsync(listArgs))
            {
                if (item.IsDir) continue;

                files.Add(new
                {
                    objectName = item.Key,
                    size = item.Size,
                    lastModified = item.LastModifiedDateTime
                });
            }

            return Ok(new
            {
                conversationId,
                files,
                count = files.Count
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing files: ConversationId={ConversationId}", conversationId);
            return StatusCode(500, new { error = "Failed to list files" });
        }
    }
}