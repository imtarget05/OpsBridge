using Microsoft.EntityFrameworkCore;
using OpsBridge.Application.InboxOutbox;
using OpsBridge.Domain.InboxOutbox;

namespace OpsBridge.Infrastructure.Persistence;

public sealed class EfOutboxRepository(OpsBridgeDbContext db) : IOutboxRepository
{
    public async Task AddAsync(OutboxMessage message, CancellationToken ct = default)
    {
        await db.OutboxMessages.AddAsync(message, ct);
        // Note: SaveChangesAsync will be called by UnitOfWork or caller
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize = 20, CancellationToken ct = default)
    {
        return await db.OutboxMessages
            .Where(o => o.Status == "Pending" && o.ProcessedAt == null)
            .OrderBy(o => o.CreatedAt)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    public async Task MarkProcessedAsync(Guid messageId, CancellationToken ct = default)
    {
        var msg = await db.OutboxMessages.FirstOrDefaultAsync(o => o.Id == messageId, ct);
        if (msg != null)
        {
            msg.Status = "Published";
            msg.ProcessedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RecordFailureAsync(Guid messageId, string error, CancellationToken ct = default)
    {
        var msg = await db.OutboxMessages.FirstOrDefaultAsync(o => o.Id == messageId, ct);
        if (msg != null)
        {
            msg.RetryCount++;
            msg.ErrorMessage = error;
            if (msg.RetryCount >= 5)
            {
                msg.Status = "Failed";
            }
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetStuckMessagesAsync(int limit = 50, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return await db.OutboxMessages
            .AsNoTracking()
            .Where(o => o.ProcessedAt == null && o.Status == "Failed")
            .OrderBy(o => o.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<int> RequeueStuckAsync(CancellationToken ct = default)
    {
        // Only terminal failures can be replayed. Pending retries are already
        // owned by the publisher and must not be reset by an operator command.
        return await db.OutboxMessages
            .Where(o => o.ProcessedAt == null && o.Status == "Failed")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, "Pending")
                .SetProperty(o => o.RetryCount, 0)
                .SetProperty(o => o.ErrorMessage, (string?)null), ct);
    }

    public async Task<bool> RequeueMessageAsync(Guid id, CancellationToken ct = default)
    {
        var changed = await db.OutboxMessages
            .Where(o => o.Id == id && o.ProcessedAt == null && o.Status == "Failed")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, "Pending")
                .SetProperty(o => o.RetryCount, 0)
                .SetProperty(o => o.ErrorMessage, (string?)null), ct);
        return changed == 1;
    }
}
