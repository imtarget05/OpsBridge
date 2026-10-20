using FlashSale.Application.Outbox;
using FlashSale.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL implementation of IOutboxRepository (Phase 10).
/// </summary>
public sealed class OutboxRepository(AppDbContext db) : IOutboxRepository
{
    /// <summary>
    /// Writes the outbox row inside the caller's transaction (ADR-019).
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> enlists in
    /// the ambient transaction, so a rollback of the business write removes the
    /// event row as well — the dual-write gap cannot come back.
    /// </summary>
    public async Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default)
    {
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetUnprocessedAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.OutboxMessages
            .Where(m => m.ProcessedAt == null
                        && m.DeadLetteredAt == null
                        && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .OrderBy(m => m.Id)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        Guid claimToken,
        int batchSize = 50,
        TimeSpan? leaseDuration = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(leaseDuration ?? TimeSpan.FromSeconds(30));
        return await db.OutboxMessages
            .FromSqlInterpolated($"""
                UPDATE "OutboxMessages"
                SET "ClaimToken" = {claimToken}, "ClaimedUntil" = {leaseUntil}
                WHERE "Id" IN (
                    SELECT "Id"
                    FROM "OutboxMessages"
                    WHERE "ProcessedAt" IS NULL
                      AND "DeadLetteredAt" IS NULL
                      AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {now})
                      AND ("ClaimedUntil" IS NULL OR "ClaimedUntil" <= {now})
                    ORDER BY "Id"
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING *
                """)
            .ToListAsync(ct);
    }

    public async Task MarkProcessedAsync(long id, Guid claimToken, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OutboxMessages"
            SET    "ProcessedAt"   = now(),
                   "NextAttemptAt" = NULL,
                   "ClaimToken"     = NULL,
                   "ClaimedUntil"   = NULL,
                   "Error"          = NULL
            WHERE  "Id" = {id}
              AND  "ClaimToken" = {claimToken}
            """, ct);
    }

    public async Task MarkFailedAsync(
        long id,
        Guid claimToken,
        string error,
        CancellationToken ct = default)
    {
        var truncated = error.Length > 500 ? error[..500] : error;
        var backoffSeconds = OutboxMessage.BackoffFor(OutboxMessage.MaxRetryCount).TotalSeconds;

        // One statement, so RetryCount cannot be read stale mid-flight.
        // - attempts left -> schedule the next try with exponential backoff
        // - attempts exhausted -> dead-letter (visible, terminal, requeueable)
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OutboxMessages"
            SET    "RetryCount"     = "RetryCount" + 1,
                   "Error"          = {truncated},
                   "NextAttemptAt"  = CASE
                                        WHEN "RetryCount" + 1 >= {OutboxMessage.MaxRetryCount} THEN NULL
                                        ELSE now() + make_interval(secs => LEAST(
                                               power(2, "RetryCount" + 1), {backoffSeconds}))
                                      END,
                   "DeadLetteredAt" = CASE
                                        WHEN "RetryCount" + 1 >= {OutboxMessage.MaxRetryCount} THEN now()
                                        ELSE "DeadLetteredAt"
                                      END,
                   "ClaimToken"     = NULL,
                   "ClaimedUntil"   = NULL
            WHERE  "Id" = {id}
              AND  "ClaimToken" = {claimToken}
            """, ct);

    }

    public async Task<IReadOnlyList<OutboxMessage>> GetStuckAsync(int limit = 50, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.OutboxMessages
            .Where(m => m.ProcessedAt == null
                        && (m.DeadLetteredAt != null || m.NextAttemptAt > now))
            .OrderByDescending(m => m.DeadLetteredAt != null)
            .ThenBy(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> CountStuckAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.OutboxMessages
            .CountAsync(m => m.ProcessedAt == null
                             && (m.DeadLetteredAt != null || m.NextAttemptAt > now), ct);
    }

    public async Task<int> RequeueStuckAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OutboxMessages"
            SET    "RetryCount"     = 0,
                   "NextAttemptAt"  = NULL,
                   "DeadLetteredAt" = NULL,
                   "Error"          = NULL,
                   "ClaimToken"     = NULL,
                   "ClaimedUntil"   = NULL
            WHERE  "ProcessedAt" IS NULL
              AND  ("DeadLetteredAt" IS NOT NULL OR "NextAttemptAt" > {now})
            """, ct);
    }
}
