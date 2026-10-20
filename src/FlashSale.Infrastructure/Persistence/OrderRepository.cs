using FlashSale.Application.Messaging;
using FlashSale.Application.Persistence;
using FlashSale.Domain;
using FlashSale.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL implementation of the order repository. The atomic conditional
/// UPDATE (ADR-002) guarantees no overselling; decrement + insert share one
/// transaction.
/// </summary>
public sealed class OrderRepository(AppDbContext db) : IOrderRepository
{
    public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default) =>
        db.Orders.AnyAsync(o => o.IdempotencyKey == idempotencyKey, ct);

    public async Task<bool> PersistAsync(OrderMessage message, CancellationToken ct = default)
    {
        // Defense in depth (Task 2, Part K): the API already returns 400 for
        // Quantity <= 0, but the repository must never mutate stock for it.
        // Without this, a negative qty would satisfy `stock >= qty` and the
        // UPDATE would ADD stock (confirmed by regression test before fix).
        if (message.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(message.Quantity), "Quantity must be positive.");

        // ADR-019: when the caller already opened a write transaction, JOIN
        // it — the order row and the outbox row must commit together. The
        // conditional stock UPDATE and the INSERT still share one transaction,
        // but the transaction may now be owned by the caller.
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Products"
            SET    "AvailableStock" = "AvailableStock" - {message.Quantity}
            WHERE  "Id" = {message.ProductId}
              AND  "AvailableStock" >= {message.Quantity}
            """, ct);

        if (affected == 0)
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(ct);
            }

            return false;
        }

        db.Orders.Add(new Order
        {
            ProductId = message.ProductId,
            Quantity = message.Quantity,
            IdempotencyKey = message.IdempotencyKey,
            CreatedAt = message.CreatedAt.UtcDateTime,
            // ADR-013 §6: null for anonymous orders, the JWT `sub` otherwise.
            UserId = message.UserId,
            // Spec §4/§5: a payment window was set at acceptance → the order
            // waits for payment; messages without one (legacy/tests) keep the
            // old terminal semantics so the pre-automation flow is unchanged.
            Status = message.PaymentDueAt is null ? OrderStatus.Completed : OrderStatus.PendingPayment,
            PaymentDueAt = message.PaymentDueAt,
            CorrelationId = message.CorrelationId
        });

        await db.SaveChangesAsync(ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }

        return true;
    }

    public async Task<int?> GetOrderIdAsync(string idempotencyKey, CancellationToken ct = default) =>
        await db.Orders
            .Where(o => o.IdempotencyKey == idempotencyKey)
            .Select(o => (int?)o.Id)
            .FirstOrDefaultAsync(ct);
}
