using FlashSale.Application.Persistence;
using FlashSale.Domain;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL implementation of the payment lifecycle port (spec §4/§5).
/// Every transition is a guarded UPDATE (WHERE Status = PendingPayment), so
/// exactly-once semantics survive at-least-once scans and duplicate API calls:
/// the second attempt matches zero rows and reports no transition.
/// </summary>
public sealed class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    public async Task<bool> CancelAndReleaseStockAsync(int orderId, CancellationToken ct = default)
    {
        // ADR-019: join an ambient write transaction when there is one — the
        // saga compensation writes the cancelled order, the restored stock and
        // the compensation events in ONE transaction.
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        // Status guard first: only a pending order can be cancelled — this is
        // what makes a duplicate/late scan a no-op instead of a double refund.
        var cancelled = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Orders"
            SET    "Status" = 'Cancelled',
                   "PaymentProcessedAt" = now(),
                   "LastPaymentResult" = 'expired'
            WHERE  "Id" = {orderId}
              AND  "Status" = 'PendingPayment'
            """, ct);
        if (cancelled == 0)
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(ct);
            }

            return false;
        }

        // Stock goes back in the SAME transaction (spec §4 "release stock"):
        // either the order is cancelled with inventory restored, or neither.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Products" p
            SET    "AvailableStock" = p."AvailableStock" + o."Quantity"
            FROM   "Orders" o
            WHERE  o."Id" = {orderId}
              AND  p."Id" = o."ProductId"
            """, ct);

        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }

        return true;
    }

    public async Task<bool> MarkPaidAsync(int orderId, CancellationToken ct = default) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Orders"
            SET    "Status" = 'Confirmed',
                   "PaymentProcessedAt" = now(),
                   "LastPaymentResult" = 'completed'
            WHERE  "Id" = {orderId}
              AND  "Status" = 'PendingPayment'
            """, ct) == 1;

    public async Task<bool> MarkPaymentFailedAsync(int orderId, CancellationToken ct = default) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Orders"
            SET    "LastPaymentResult" = 'failed'
            WHERE  "Id" = {orderId}
              AND  "Status" = 'PendingPayment'
            """, ct) == 1;

    public async Task<int> IncrementReminderAsync(int orderId, CancellationToken ct = default) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Orders"
            SET    "PaymentReminderCount" = "PaymentReminderCount" + 1
            WHERE  "Id" = {orderId}
              AND  "Status" = 'PendingPayment'
            """, ct);

    public async Task<PaymentOrderView?> GetByKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
        await db.Orders.AsNoTracking()
            .Where(o => o.IdempotencyKey == idempotencyKey)
            .Select(o => new PaymentOrderView(
                o.Id, o.ProductId, o.Quantity, o.IdempotencyKey, o.Status, o.UserId))
            .FirstOrDefaultAsync(ct);
}