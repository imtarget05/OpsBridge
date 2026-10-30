using FlashSale.Domain.Outbox;

namespace FlashSale.Application.Outbox;

/// <summary>
/// Port for Transactional Outbox persistence and retrieval.
/// </summary>
public interface IOutboxRepository
{
    Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default);

    /// <summary>Includes pending, published and dead-lettered rows so a repair
    /// never creates a second business event when an older row still exists.</summary>
    Task<bool> HasEventAsync(string eventType, string aggregateType, string aggregateId, Guid correlationId, CancellationToken ct = default);

    /// <summary>
    /// Rows eligible RIGHT NOW: not processed, not dead-lettered, and past
    /// their backoff deadline (NextAttemptAt is null or already due).
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> GetUnprocessedAsync(int batchSize = 50, CancellationToken ct = default);

    /// <summary>
    /// Atomically leases eligible rows to one dispatcher. Multiple API replicas
    /// may poll concurrently without publishing the same row at the same time.
    /// A lease that expires (dispatcher crash) makes the row claimable again —
    /// nothing is stranded.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        Guid claimToken,
        int batchSize = 50,
        TimeSpan? leaseDuration = null,
        CancellationToken ct = default);

    Task MarkProcessedAsync(long id, Guid claimToken, CancellationToken ct = default);

    /// <summary>
    /// Records a failed attempt: increments RetryCount, applies exponential
    /// backoff to NextAttemptAt, and dead-letters the row once
    /// <see cref="FlashSale.Domain.Outbox.OutboxMessage.MaxRetryCount"/> is reached.
    /// </summary>
    Task<OutboxFailureOutcome> MarkFailedAsync(long id, Guid claimToken, string error, CancellationToken ct = default);

    /// <summary>
    /// Rows that are NOT progressing: dead-lettered, or waiting on a backoff
    /// deadline. This is the honest counterpart to pending — a row stuck at
    /// the retry ceiling must never be reported as "no pending messages".
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> GetStuckAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Lookup a single outbox row by its stable MessageId.
    /// Read-only: used by the agent investigation workflow (§3.1 §2).
    /// </summary>
    Task<OutboxMessage?> GetByMessageIdAsync(Guid messageId, CancellationToken ct = default);

    /// <summary>Count only, for cheap health/metrics probes.</summary>
    Task<int> CountStuckAsync(CancellationToken ct = default);

    /// <summary>
    /// Rows that are written but not yet published (including rows waiting on
    /// a backoff deadline). This is the OUTBOX LAG the operator has to watch.
    /// </summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// Operator recovery: clears the retry/backoff/dead-letter state so the
    /// dispatcher picks the rows up again. Returns rows revived.
    /// </summary>
    Task<int> RequeueStuckAsync(CancellationToken ct = default);
}

/// <summary>
/// What happened to a failed publish attempt (ADR-019). The dispatcher needs
/// it to report retry/dead-letter counts — a poisoned row must never fail
/// silently.
/// </summary>
/// <param name="RetryCount">Attempts made so far, after this failure.</param>
/// <param name="NextAttemptAt">When the next attempt is due; NULL once dead-lettered.</param>
/// <param name="DeadLettered">True when the retry ceiling was reached.</param>
public sealed record OutboxFailureOutcome(
    int RetryCount,
    DateTimeOffset? NextAttemptAt,
    bool DeadLettered);
