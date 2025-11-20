namespace Chat.Api.Infrastructure.Storage
{
    public class StoredFileInfo
    {
        public Guid FileId { get; init; } = default!;
        public string Bucket { get; init; } = default!;
        public string ObjectKey { get; init; } = default!;
        public long Size { get; init; }
        public string ContentType { get; init; } = default!;
        public string ChecksumSha256 { get; init; } = default!;
    }
}
