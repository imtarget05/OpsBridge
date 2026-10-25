using FlashSale.Application.Outbox;
using FlashSale.Domain.Entities;
using FlashSale.Domain.Outbox;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.IntegrationTests;

/// <summary>Real PostgreSQL proof of the alert's unique-index race backstop.</summary>
[Collection("outbox-crash-window")]
public sealed class StuckOrderIntegrationTests(OutboxCrashWindowFixture fixture)
{
    [Fact]
    public async Task OverdueReservation_AlertsOnceAcrossConcurrentScans_ThenDisappearsAfterOrderPersists()
    {
        var productId = await fixture.ResetDatabaseAsync();
        var acceptedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var reservationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var key = $"stuck-order-{Guid.NewGuid():N}";

        await using (var seed = fixture.CreateDbContext())
        {
            seed.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = reservationId,
                EventType = "inventory.reserved",
                Topic = OutboxEventWriter.EventExchange,
                AggregateType = OutboxEventWriter.OrderAggregate,
                AggregateId = key,
                CorrelationId = correlationId,
                CreatedAt = acceptedAt
            });
            await seed.SaveChangesAsync();
        }

        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var first = new StuckOrderRepository(firstDb,
            new OutboxEventWriter(new OutboxRepository(firstDb)));
        var second = new StuckOrderRepository(secondDb,
            new OutboxEventWriter(new OutboxRepository(secondDb)));
        var candidate = Assert.Single(await first.FindUnfulfilledAsync(
            DateTimeOffset.UtcNow.AddMinutes(-5), 200));

        var results = await Task.WhenAll(
            first.TryRaiseAlertAsync(candidate, DateTimeOffset.UtcNow, 5),
            second.TryRaiseAlertAsync(candidate, DateTimeOffset.UtcNow, 5));
        Assert.Single(results, raised => raised);

        await using (var verify = fixture.CreateDbContext())
        {
            Assert.Equal(1, await verify.OutboxMessages.CountAsync(row =>
                row.EventType == "order.fulfillment_stuck" && row.AggregateId == key));
            verify.Orders.Add(new Order
            {
                ProductId = productId,
                Quantity = 1,
                IdempotencyKey = key,
                CreatedAt = DateTime.UtcNow
            });
            await verify.SaveChangesAsync();
        }

        await using var recoveredDb = fixture.CreateDbContext();
        var recovered = new StuckOrderRepository(recoveredDb,
            new OutboxEventWriter(new OutboxRepository(recoveredDb)));
        Assert.Empty(await recovered.FindUnfulfilledAsync(DateTimeOffset.UtcNow, 200));
    }
}
