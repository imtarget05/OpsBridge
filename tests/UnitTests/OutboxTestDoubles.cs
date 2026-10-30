using FlashSale.Application.Outbox;
using FlashSale.Application.Persistence;
using FlashSale.Domain.Outbox;

namespace FlashSale.UnitTests;

/// <summary>
/// Test double for the ambient write transaction (ADR-019). Records the
/// begin/commit/rollback calls so a test can assert that a use case really
/// wraps its state change and its outbox row into one transaction.
/// </summary>
internal sealed class FakeWriteTransaction : IWriteTransaction
{
    public int BeginCalls { get; private set; }
    public int CommitCalls { get; private set; }
    public int RollbackCalls { get; private set; }
    public bool InTransaction { get; private set; }

    public Task BeginAsync(CancellationToken ct = default)
    {
        BeginCalls++;
        InTransaction = true;
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken ct = default)
    {
        CommitCalls++;
        InTransaction = false;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct = default)
    {
        RollbackCalls++;
        InTransaction = false;
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory outbox repository: captures the rows a use case writes, so the
/// unit tests can assert that the event went to the OUTBOX (and not to the
/// broker) inside the business transaction.
/// </summary>
internal sealed class FakeOutboxRepository : IOutboxRepository
{
    public List<OutboxMessage> Enqueued { get; } = new();

    /// <summary>Set to make the enqueue fail (simulates a DB write error).</summary>
    public Exception? EnqueueFailure { get; set; }

    /// <summary>Optional predicate to trigger conditional enqueue failures.</summary>
    public Func<OutboxMessage, Exception?>? EnqueueFailureFactory { get; set; }

    public Func<Exception?>? LookupFailureFactory { get; set; }

    public Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default)
    {
        var ex = EnqueueFailureFactory?.Invoke(message) ?? EnqueueFailure;
        if (ex is not null)
        {
            return Task.FromException(ex);
        }

        if (Enqueued.Any(existing => existing.MessageId == message.MessageId))
            return Task.FromException(new DuplicateOutboxMessageException(message.MessageId));

        Enqueued.Add(message);
        return Task.CompletedTask;
    }

    public Task<bool> HasEventAsync(string eventType, string aggregateType, string aggregateId, Guid correlationId, CancellationToken ct = default)
    {
        var failure = LookupFailureFactory?.Invoke();
        if (failure is not null) return Task.FromException<bool>(failure);
        return Task.FromResult(Enqueued.Any(m => m.EventType == eventType && m.AggregateType == aggregateType && m.AggregateId == aggregateId
                                               && m.CorrelationId == correlationId));
    }

    public Task<IReadOnlyList<OutboxMessage>> GetUnprocessedAsync(int batchSize = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OutboxMessage>>(Enqueued);

    public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        Guid claimToken,
        int batchSize = 50,
        TimeSpan? leaseDuration = null,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());

    public Task MarkProcessedAsync(long id, Guid claimToken, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<OutboxFailureOutcome> MarkFailedAsync(long id, Guid claimToken, string error, CancellationToken ct = default)
        => Task.FromResult(new OutboxFailureOutcome(0, null, false));

    public Task<IReadOnlyList<OutboxMessage>> GetStuckAsync(int limit = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());

    public Task<OutboxMessage?> GetByMessageIdAsync(Guid messageId, CancellationToken ct = default)
        => Task.FromResult(Enqueued.FirstOrDefault(m => m.MessageId == messageId));
    public Task<int> CountStuckAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task<int> CountPendingAsync(CancellationToken ct = default) => Task.FromResult(Enqueued.Count);

    public Task<int> RequeueStuckAsync(CancellationToken ct = default) => Task.FromResult(0);

    /// <summary>Event types written to the outbox, in order.</summary>
    public List<string> EnqueuedEventTypes => Enqueued.Select(m => m.EventType).ToList();
}
