using FlashSale.Application.Saga;
using FlashSale.Domain.Saga;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL EF Core implementation of ICheckoutSagaRepository (Phases 9 & 11).
/// </summary>
public sealed class CheckoutSagaRepository(AppDbContext db) : ICheckoutSagaRepository
{
    public Task<CheckoutSagaState?> GetBySagaIdAsync(Guid sagaId, CancellationToken ct = default) =>
        db.CheckoutSagas.FirstOrDefaultAsync(s => s.SagaId == sagaId, ct);

    public Task<CheckoutSagaState?> GetByOrderIdAsync(int orderId, CancellationToken ct = default) =>
        db.CheckoutSagas.FirstOrDefaultAsync(s => s.OrderId == orderId, ct);

    public Task<CheckoutSagaState?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
        db.CheckoutSagas.FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, ct);

    public async Task SaveAsync(CheckoutSagaState saga, CancellationToken ct = default)
    {
        var existing = await db.CheckoutSagas.FirstOrDefaultAsync(s => s.SagaId == saga.SagaId, ct);
        if (existing is null)
        {
            db.CheckoutSagas.Add(saga);
        }
        else
        {
            if (existing.Version != saga.Version)
            {
                throw new DbUpdateConcurrencyException(
                    $"Optimistic concurrency violation: CheckoutSaga {saga.SagaId} presented version {saga.Version} but database version is {existing.Version}.");
            }

            db.Entry(existing).OriginalValues[nameof(CheckoutSagaState.Version)] = saga.Version;

            existing.OrderId = saga.OrderId;
            existing.Status = saga.Status;
            existing.InventoryStatus = saga.InventoryStatus;
            existing.PaymentStatus = saga.PaymentStatus;
            existing.FailureReason = saga.FailureReason;
            existing.CompensationReason = saga.CompensationReason;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.Version++;
        }

        await db.SaveChangesAsync(ct);
        saga.Version = existing?.Version ?? saga.Version;
    }
}
