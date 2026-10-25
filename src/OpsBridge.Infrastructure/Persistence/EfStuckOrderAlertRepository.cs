using Microsoft.EntityFrameworkCore;
using OpsBridge.Application.StuckOrder;
using OpsBridge.Domain.StuckOrder;

namespace OpsBridge.Infrastructure.Persistence;

public sealed class EfStuckOrderAlertRepository(OpsBridgeDbContext db) : IStuckOrderAlertRepository
{
    public async Task AddAsync(StuckOrderAlert alert, CancellationToken ct = default)
    {
        await db.Set<StuckOrderAlert>().AddAsync(alert, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<StuckOrderAlert?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await db.Set<StuckOrderAlert>()
            .FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<StuckOrderAlert?> GetByPurchaseOrderIdAsync(Guid purchaseOrderId, CancellationToken ct = default)
    {
        return await db.Set<StuckOrderAlert>()
            .FirstOrDefaultAsync(a => a.PurchaseOrderId == purchaseOrderId, ct);
    }

    public async Task<IReadOnlyList<StuckOrderAlert>> GetOpenAlertsAsync(CancellationToken ct = default)
    {
        return await db.Set<StuckOrderAlert>()
            .Where(a => a.Status == StuckOrderStatus.Open || a.Status == StuckOrderStatus.UnderInvestigation)
            .OrderByDescending(a => a.DetectedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StuckOrderAlert>> GetAlertsByStatusAsync(StuckOrderStatus status, CancellationToken ct = default)
    {
        return await db.Set<StuckOrderAlert>()
            .Where(a => a.Status == status)
            .OrderByDescending(a => a.DetectedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StuckOrderAlert>> GetDeadLetteredAlertsAsync(CancellationToken ct = default)
    {
        return await db.Set<StuckOrderAlert>()
            .Where(a => a.IsDeadLettered)
            .OrderByDescending(a => a.DetectedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StuckOrderAlert>> GetAlertsForRetryAsync(TimeSpan retryInterval, int maxRetries, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(retryInterval);
        return await db.Set<StuckOrderAlert>()
            .Where(a => a.Status == StuckOrderStatus.Open
                && a.RetryCount < maxRetries
                && (a.LastRetryAt == null || a.LastRetryAt <= cutoff))
            .OrderBy(a => a.DetectedAt)
            .ToListAsync(ct);
    }

    public async Task UpdateAsync(StuckOrderAlert alert, CancellationToken ct = default)
    {
        db.Set<StuckOrderAlert>().Update(alert);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> ExistsForPurchaseOrderAsync(Guid purchaseOrderId, CancellationToken ct = default)
    {
        return await db.Set<StuckOrderAlert>()
            .AnyAsync(a => a.PurchaseOrderId == purchaseOrderId, ct);
    }
}
