using FlashSale.Application.Outbox;
using FlashSale.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL implementation of IInboxRepository for deduplication (Phase 10, ADR-019).
/// </summary>
public sealed class InboxRepository(AppDbContext db) : IInboxRepository
{
    public async Task<bool> HasBeenProcessedAsync(Guid messageId, string consumerName, CancellationToken ct = default)
    {
        return await db.InboxMessages.AnyAsync(
            i => i.MessageId == messageId && i.ConsumerName == consumerName && i.ProcessedAt != null, ct);
    }

    /// <summary>
    /// Mark a message as processed. Idempotent: inserts the row when it is
    /// missing (operator path) and updates it when the consumer already
    /// claimed it.
    /// </summary>
    public async Task MarkProcessedAsync(Guid messageId, string consumerName, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InboxMessages" ("MessageId", "ConsumerName", "ProcessedAt", "ClaimedAt")
            VALUES ({messageId}, {consumerName}, {now}, NULL)
            ON CONFLICT ("MessageId", "ConsumerName")
            DO UPDATE SET "ProcessedAt" = {now}, "ClaimedAt" = NULL
            """, ct);
    }

    /// <summary>
    /// Claim a message id before applying side effects (ADR-019).
    ///
    /// <para>
    /// The claim is an INSERT guarded by the unique constraint on
    /// (MessageId, ConsumerName): exactly one caller can win, so two
    /// concurrent deliveries of the same event cannot both apply side
    /// effects. A claim left behind by a crashed consumer is taken over once
    /// the lease expired, so a crash mid-processing does not lose the event.
    /// </para>
    /// </summary>
    public async Task<bool> TryClaimAsync(
        Guid messageId,
        string consumerName,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "InboxMessages" ("MessageId", "ConsumerName", "ProcessedAt", "ClaimedAt")
            VALUES ({messageId}, {consumerName}, NULL, {now})
            ON CONFLICT ("MessageId", "ConsumerName") DO NOTHING
            """, ct);

        if (inserted > 0)
        {
            return true;
        }

        var state = await db.InboxMessages
            .AsNoTracking()
            .Where(i => i.MessageId == messageId && i.ConsumerName == consumerName)
            .Select(i => new { i.ProcessedAt, i.ClaimedAt })
            .FirstOrDefaultAsync(ct);

        if (state is null)
        {
            return false;
        }

        // Already processed -> duplicate delivery.
        if (state.ProcessedAt is not null)
        {
            return false;
        }

        // Claimed by a live worker -> in flight, not ours.
        var leaseCutoff = now - InboxMessage.ClaimLease;
        if (state.ClaimedAt is not null && state.ClaimedAt > leaseCutoff)
        {
            return false;
        }

        // Stale claim (crashed consumer) -> take it over.
        var takenOver = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "InboxMessages"
            SET    "ClaimedAt" = {now}
            WHERE  "MessageId" = {messageId}
              AND  "ConsumerName" = {consumerName}
              AND  "ProcessedAt" IS NULL
              AND  ("ClaimedAt" IS NULL OR "ClaimedAt" <= {leaseCutoff})
            """, ct);

        return takenOver > 0;
    }
}
