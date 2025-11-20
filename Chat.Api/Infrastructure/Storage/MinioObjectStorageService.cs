namespace Chat.Api.Infrastructure.Storage
{
    using System.Security.Cryptography;
    using Minio;
    using Minio.DataModel.Args;

    public class MinioObjectStorageService : IObjectStorageService
    {
        private readonly MinioClient _client;
        private readonly ILogger<MinioObjectStorageService> _logger;
        private readonly string _bucketName;

        public MinioObjectStorageService(
            MinioClient client,
            IConfiguration config)
        {
            _client = client;
            //_logger = logger;
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

            // file_id = Guid
            var fileId = Guid.NewGuid();

            // Object key: opcionalmente incluir conversationId
            var safeFileName = Path.GetFileName(fileName);
            var objectKey = $"{conversationId ?? Guid.Empty}/{fileId}/{safeFileName}";

            // Calcula checksum SHA-256 enquanto copia para um MemoryStream
            // (em produção, ideal streamar direto e calcular no fluxo para arquivos realmente grandes)
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

            // Limite de 2GB (PDF)
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
                .WithExpiry((int)expiry.TotalSeconds); // em segundos

            var url = await _client.PresignedGetObjectAsync(args);
            return url;
        }
    }

}
