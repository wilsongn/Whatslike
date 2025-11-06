using Chat.Persistence.Models;

namespace Chat.Persistence.Abstractions;

public interface IMessageStore
{
    Task<long> InsertMessageAsync(MessageRecord msg, CancellationToken ct = default);
    Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid organizacaoId, Guid conversaId, int bucket, long? fromSeq, int limit, CancellationToken ct = default);
    int ComputeBucket(DateTimeOffset utcCreatedAt);
    Task UpdateMessageStatusAsync(Guid organizacaoId, Guid conversaId, int bucket, long sequencia, string status);

}
