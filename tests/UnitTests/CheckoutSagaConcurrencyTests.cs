using FlashSale.Domain.Saga;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlashSale.UnitTests;

/// <summary>
/// G5 Verification: Tests that the optimistic-concurrency <see cref="CheckoutSagaState.Version"/>
/// token configured in <see cref="AppDbContext"/> (<c>entity.Property(s => s.Version).IsConcurrencyToken()</c>)
/// and <see cref="CheckoutSagaRepository"/> actively detects conflicting concurrent updates,
/// throws <see cref="DbUpdateConcurrencyException"/>, and guarantees no lost updates.
/// </summary>
public class CheckoutSagaConcurrencyTests
{
    private static AppDbContext CreateInMemoryDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task SaveAsync_WhenConcurrentUpdateHasStaleVersion_ThrowsDbUpdateConcurrencyException_AndPreventsLostUpdates()
    {
        var dbName = Guid.NewGuid().ToString("N");

        // 1. Arrange: Seed initial saga with Version = 1
        var sagaId = Guid.NewGuid();
        var initialSaga = new CheckoutSagaState
        {
            SagaId = sagaId,
            IdempotencyKey = "saga-concurrent-001",
            ProductId = 101,
            Quantity = 1,
            Amount = 49.99m,
            Status = SagaStatus.Started,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using (var db = CreateInMemoryDb(dbName))
        {
            var repo = new CheckoutSagaRepository(db);
            await repo.SaveAsync(initialSaga);
        }

        // 2. Simulate two concurrent sessions loading the same saga at Version 1
        CheckoutSagaState? sagaSession1;
        CheckoutSagaState? sagaSession2;

        await using (var db1 = CreateInMemoryDb(dbName))
        {
            var repo1 = new CheckoutSagaRepository(db1);
            sagaSession1 = await repo1.GetBySagaIdAsync(sagaId);
        }

        await using (var db2 = CreateInMemoryDb(dbName))
        {
            var repo2 = new CheckoutSagaRepository(db2);
            sagaSession2 = await repo2.GetBySagaIdAsync(sagaId);
        }

        Assert.NotNull(sagaSession1);
        Assert.NotNull(sagaSession2);
        Assert.Equal(1, sagaSession1.Version);
        Assert.Equal(1, sagaSession2.Version);

        // 3. Session 1 wins: modifies status to InventoryReserved and saves (Version 1 -> 2)
        sagaSession1.Status = SagaStatus.InventoryReserved;
        sagaSession1.InventoryStatus = InventoryReservationStatus.Reserved;

        await using (var db1 = CreateInMemoryDb(dbName))
        {
            var repo1 = new CheckoutSagaRepository(db1);
            await repo1.SaveAsync(sagaSession1);
        }

        Assert.Equal(2, sagaSession1.Version);

        // 4. Session 2 loses: attempts to save with stale Version 1
        sagaSession2.Status = SagaStatus.PaymentProcessing;
        sagaSession2.PaymentStatus = PaymentTransactionStatus.Pending;

        await using (var db2 = CreateInMemoryDb(dbName))
        {
            var repo2 = new CheckoutSagaRepository(db2);
            var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => repo2.SaveAsync(sagaSession2));
            Assert.Contains("Optimistic concurrency violation", ex.Message);
        }

        // 5. Verify database integrity: Session 1's write is preserved, Session 2 did NOT overwrite (no lost update)
        await using (var verifyDb = CreateInMemoryDb(dbName))
        {
            var repo = new CheckoutSagaRepository(verifyDb);
            var finalState = await repo.GetBySagaIdAsync(sagaId);

            Assert.NotNull(finalState);
            Assert.Equal(SagaStatus.InventoryReserved, finalState.Status);
            Assert.Equal(InventoryReservationStatus.Reserved, finalState.InventoryStatus);
            Assert.Equal(2, finalState.Version);
        }
    }

    [Fact]
    public async Task SaveAsync_SequentialUpdates_IncrementVersionMonotonically()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var saga = new CheckoutSagaState
        {
            SagaId = Guid.NewGuid(),
            IdempotencyKey = "saga-seq-001",
            ProductId = 202,
            Quantity = 2,
            Amount = 99.00m,
            Status = SagaStatus.Started,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Create
        await using (var db = CreateInMemoryDb(dbName))
        {
            var repo = new CheckoutSagaRepository(db);
            await repo.SaveAsync(saga);
        }
        Assert.Equal(1, saga.Version);

        // Update 1: Started -> InventoryReserved
        saga.Status = SagaStatus.InventoryReserved;
        await using (var db = CreateInMemoryDb(dbName))
        {
            var repo = new CheckoutSagaRepository(db);
            await repo.SaveAsync(saga);
        }
        Assert.Equal(2, saga.Version);

        // Update 2: InventoryReserved -> Completed
        saga.Status = SagaStatus.Completed;
        saga.OrderId = 54321;
        await using (var db = CreateInMemoryDb(dbName))
        {
            var repo = new CheckoutSagaRepository(db);
            await repo.SaveAsync(saga);
        }
        Assert.Equal(3, saga.Version);

        // Verify persisted state
        await using (var db = CreateInMemoryDb(dbName))
        {
            var repo = new CheckoutSagaRepository(db);
            var loaded = await repo.GetBySagaIdAsync(saga.SagaId);

            Assert.NotNull(loaded);
            Assert.Equal(SagaStatus.Completed, loaded.Status);
            Assert.Equal(54321, loaded.OrderId);
            Assert.Equal(3, loaded.Version);
        }
    }

    [Fact]
    public async Task FakeCheckoutSagaRepository_WhenStaleVersionPresented_ThrowsDbUpdateConcurrencyException()
    {
        var repo = new FakeCheckoutSagaRepository();
        var saga = new CheckoutSagaState
        {
            SagaId = Guid.NewGuid(),
            IdempotencyKey = "fake-concurrent-001",
            ProductId = 303,
            Quantity = 1,
            Amount = 25.00m,
            Status = SagaStatus.Started,
            Version = 1
        };

        // Initial save
        await repo.SaveAsync(saga);
        Assert.Equal(1, saga.Version);

        // First update advances version to 2
        saga.Status = SagaStatus.InventoryReserved;
        await repo.SaveAsync(saga);
        Assert.Equal(2, saga.Version);

        // Stale update presenting Version 1 must throw
        var staleSaga = new CheckoutSagaState
        {
            SagaId = saga.SagaId,
            IdempotencyKey = saga.IdempotencyKey,
            ProductId = saga.ProductId,
            Quantity = saga.Quantity,
            Amount = saga.Amount,
            Status = SagaStatus.Failed,
            Version = 1
        };

        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => repo.SaveAsync(staleSaga));
        Assert.Contains("Optimistic concurrency violation", ex.Message);
    }
}
