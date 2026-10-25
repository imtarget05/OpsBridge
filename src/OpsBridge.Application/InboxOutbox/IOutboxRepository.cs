using OpsBridge.Domain.InboxOutbox;

namespace OpsBridge.Application.InboxOutbox;

public interface IOutboxRepository
{
    Task AddAsync(OutboxMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize = 20, CancellationToken ct = default);
    Task MarkProcessedAsync(Guid messageId, CancellationToken ct = default);
    Task RecordFailureAsync(Guid messageId, string error, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetStuckMessagesAsync(int limit = 50, CancellationToken ct = default);
    Task<int> RequeueStuckAsync(CancellationToken ct = default);
    Task<bool> RequeueMessageAsync(Guid id, CancellationToken ct = default);
}
