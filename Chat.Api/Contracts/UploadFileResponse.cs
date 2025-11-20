namespace Chat.Api.Contracts
{
    public class UploadFileResponse
    {
        public Guid FileId { get; set; } = default!;
        public Guid ConversationId { get; set; } = default!;
        public long Size { get; set; }
        public string ContentType { get; set; } = default!;
        public string ChecksumSha256 { get; set; } = default!;
    }
}
