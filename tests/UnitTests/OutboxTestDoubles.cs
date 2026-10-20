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

    public Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default)
    {
        if (EnqueueFailure is not null)
        {
            return Task.FromException(EnqueueFailure);
        }

        Enqueued.Add(message);
        return Task.CompletedTask;
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

    public Task MarkFailedAsync(long id, Guid claimToken, string error, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<OutboxMessage>> GetStuckAsync(int limit = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());

    public Task<int> CountStuckAsync(CancellationToken ct = default) => Task.FromResult(0);

    public Task<int> RequeueStuckAsync(CancellationToken ct = default) => Task.FromResult(0);

    /// <summary>Event types written to the outbox, in order.</summary>
    public List<string> EnqueuedEventTypes => Enqueued.Select(m => m.EventType).ToList();
}
