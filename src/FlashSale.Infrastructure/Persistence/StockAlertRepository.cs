using FlashSale.Application.Persistence;
using FlashSale.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL implementation of the low-stock alert port (spec §6).
/// Dedupe is two-layered: a pre-check SELECT plus the partial unique index
/// (ProductId WHERE Status='Open') as the race backstop — a concurrent scan
/// loses the insert and is reported as "already alerted", never as an error.
/// </summary>
public sealed class StockAlertRepository(AppDbContext db) : IStockAlertRepository
{
    public async Task<IReadOnlyList<LowStockProductView>> GetLowStockProductsAsync(
        int defaultThreshold, int take, CancellationToken ct = default) =>
        await db.Products.AsNoTracking()
            .Where(p => p.AvailableStock <=
                (p.ReorderThreshold > 0 ? p.ReorderThreshold : defaultThreshold))
            .OrderBy(p => p.AvailableStock)
            .Take(take)
            .Select(p => new LowStockProductView(p.Id, p.Name, p.AvailableStock, p.ReorderThreshold))
            .ToListAsync(ct);

    public async Task<StockAlert?> TryCreateAsync(StockAlert alert, CancellationToken ct = default)
    {
        var alreadyOpen = await db.StockAlerts
            .AnyAsync(a => a.ProductId == alert.ProductId && a.Status == StockAlertStatus.Open, ct);
        if (alreadyOpen) return null;

        db.StockAlerts.Add(alert);

        // ADR-019: the alert and its inventory.low_stock event are written in
        // ONE transaction. Inside a transaction, a unique-index violation
        // ABORTS it in PostgreSQL — every following statement fails with
        // "current transaction is aborted" until the transaction ends. A
        // savepoint makes the dedup race survivable: rolling back to it
        // restores the transaction, so the scan can carry on and still commit
        // the events of the products it did alert on.
        var transaction = db.Database.CurrentTransaction;
        var hasSavepoint = transaction is not null;
        if (transaction is not null)
        {
            await transaction.CreateSavepointAsync("stock_alert_dedupe", ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return alert;
        }
        catch (DbUpdateException)
        {
            // Partial unique index rejected a concurrent insert — deduped, not fatal.
            if (hasSavepoint)
            {
                await transaction!.RollbackToSavepointAsync("stock_alert_dedupe", ct);
            }

            db.Entry(alert).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<IReadOnlyList<StockAlert>> ListOpenAsync(int take, CancellationToken ct = default) =>
        await db.StockAlerts.AsNoTracking()
            .Where(a => a.Status == StockAlertStatus.Open)
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
}