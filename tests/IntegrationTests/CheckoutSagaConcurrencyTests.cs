using FlashSale.Domain.Saga;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlashSale.IntegrationTests;

/// <summary>
/// G5 Verification (PostgreSQL Integration): Verifies that PostgreSQL + EF Core
/// enforce the optimistic concurrency token on <see cref="CheckoutSagaState.Version"/>,
/// rejecting stale updates with <see cref="DbUpdateConcurrencyException"/> and preventing
/// lost updates under database concurrency.
/// </summary>
[Collection("flashsale")]
public sealed class CheckoutSagaConcurrencyTests(FlashSaleFixture fx)
{
    [Fact]
    public async Task Postgres_CheckoutSaga_EnforcesOptimisticConcurrencyToken_ThrowsOnStaleVersion()
    {
        await fx.ResetDatabaseAsync(stock: 10);

        var sagaId = Guid.NewGuid();
        var key = $"saga-pg-concurrency-{Guid.NewGuid():N}";

        // 1. Arrange: Insert saga into real PostgreSQL at Version 1
        await using (var db = fx.CreateDbContext())
        {
            var repo = new CheckoutSagaRepository(db);
            var initial = new CheckoutSagaState
            {
                SagaId = sagaId,
                IdempotencyKey = key,
                ProductId = 1,
                Quantity = 1,
                Amount = 150000m,
                Status = SagaStatus.Started,
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await repo.SaveAsync(initial);
        }

        // 2. Concurrently read saga in two isolated DbContext scopes
        CheckoutSagaState? saga1;
        CheckoutSagaState? saga2;

        await using (var db1 = fx.CreateDbContext())
        {
            var repo1 = new CheckoutSagaRepository(db1);
            saga1 = await repo1.GetBySagaIdAsync(sagaId);
        }

        await using (var db2 = fx.CreateDbContext())
        {
            var repo2 = new CheckoutSagaRepository(db2);
            saga2 = await repo2.GetBySagaIdAsync(sagaId);
        }

        Assert.NotNull(saga1);
        Assert.NotNull(saga2);
        Assert.Equal(1, saga1.Version);
        Assert.Equal(1, saga2.Version);

        // 3. First transaction commits: advances status to InventoryReserved (Version 1 -> 2)
        saga1.Status = SagaStatus.InventoryReserved;
        saga1.InventoryStatus = InventoryReservationStatus.Reserved;

        await using (var db1 = fx.CreateDbContext())
        {
            var repo1 = new CheckoutSagaRepository(db1);
            await repo1.SaveAsync(saga1);
        }
        Assert.Equal(2, saga1.Version);

        // 4. Second transaction tries to commit with stale Version 1: PostgreSQL/EF Core rejects with DbUpdateConcurrencyException
        saga2.Status = SagaStatus.PaymentProcessing;

        await using (var db2 = fx.CreateDbContext())
        {
            var repo2 = new CheckoutSagaRepository(db2);
            var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => repo2.SaveAsync(saga2));
            Assert.Contains("Optimistic concurrency violation", ex.Message);
        }

        // 5. Verify PostgreSQL row: Version is 2, Status is InventoryReserved (no lost update)
        await using (var verifyDb = fx.CreateDbContext())
        {
            var repo = new CheckoutSagaRepository(verifyDb);
            var actual = await repo.GetBySagaIdAsync(sagaId);

            Assert.NotNull(actual);
            Assert.Equal(SagaStatus.InventoryReserved, actual.Status);
            Assert.Equal(2, actual.Version);
        }
    }
}
