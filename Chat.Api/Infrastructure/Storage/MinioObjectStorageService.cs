using System.Security.Cryptography;
using Minio;
using Minio.DataModel.Args;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Chat.Api.Infrastructure.Storage
{
    public class MinioObjectStorageService : IObjectStorageService
    {
        // Alterado de MinioClient para IMinioClient (Interface)
        private readonly IMinioClient _client;
        private readonly ILogger<MinioObjectStorageService> _logger;
        private readonly string _bucketName;

        public MinioObjectStorageService(
            IMinioClient client,                  // <--- Recebe a Interface agora
            ILogger<MinioObjectStorageService> logger, // <--- Adicionado Logger corretamente
            IConfiguration config)
        {
            _client = client;
            _logger = logger;
            _bucketName = config["Minio:BucketName"] ?? "whatslike-files";
        }

        public async Task<StoredFileInfo> UploadAsync(
            Stream stream,
            string fileName,
            string contentType,
            Guid? conversationId,
            Guid? uploaderId,
            CancellationToken ct = default)
        {
            // Garante que o bucket exista
            bool found = await _client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(_bucketName),
                ct);

            if (!found)
            {
                await _client.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(_bucketName),
                    ct);
            }

            var fileId = Guid.NewGuid();
            var safeFileName = Path.GetFileName(fileName);
            var objectKey = $"{conversationId ?? Guid.Empty}/{fileId}/{safeFileName}";

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            ms.Position = 0;

            string checksumSha256;
            using (var sha = SHA256.Create())
            {
                var hash = await sha.ComputeHashAsync(ms, ct);
                checksumSha256 = Convert.ToHexString(hash).ToLowerInvariant();
            }

            ms.Position = 0;
            var size = ms.Length;

            const long maxSize = 2L * 1024 * 1024 * 1024;
            if (size > maxSize)
            {
                throw new InvalidOperationException("Arquivo excede o limite de 2GB.");
            }

            var putArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectKey)
                .WithStreamData(ms)
                .WithObjectSize(size)
                .WithContentType(contentType);

            await _client.PutObjectAsync(putArgs, ct);

            _logger.LogInformation(
                "Arquivo {FileId} enviado para bucket {Bucket} com key {ObjectKey}, size={Size}",
                fileId, _bucketName, objectKey, size);

            return new StoredFileInfo
            {
                FileId = fileId,
                Bucket = _bucketName,
                ObjectKey = objectKey,
                Size = size,
                ContentType = contentType,
                ChecksumSha256 = checksumSha256
            };
        }

        public async Task<string> GetDownloadUrlAsync(
            string bucket,
            string objectKey,
            TimeSpan expiry,
            CancellationToken ct = default)
        {
            var args = new PresignedGetObjectArgs()
                .WithBucket(bucket)
                .WithObject(objectKey)
                .WithExpiry((int)expiry.TotalSeconds);

            var url = await _client.PresignedGetObjectAsync(args);
            return url;
        }
    }
}
