namespace Chat.Api.Infrastructure.Storage
{
    public interface IObjectStorageService
    {
        Task<StoredFileInfo> UploadAsync(
        Stream stream,
        string fileName,
        string contentType,
        Guid? conversationId,
        Guid? uploaderId,
        CancellationToken ct = default);

        Task<string> GetDownloadUrlAsync(
            string bucket,
            string objectKey,
            TimeSpan expiry,
            CancellationToken ct = default);
    }
}
