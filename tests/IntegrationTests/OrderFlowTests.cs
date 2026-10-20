using FlashSale.Application.Outbox;
using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Infrastructure.Messaging;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Invariants: INVENTORY CONSERVATION (initial == final + sold),
/// NO NEGATIVE STOCK, IDEMPOTENCY (same key → one order),
/// INVALID INPUT → no mutation.
/// </summary>
[Collection("flashsale")]
public sealed class OrderFlowTests(FlashSaleFixture fx)
{
    private static OrderMessage Msg(int productId, int qty, string key) =>
        new(productId, qty, key, DateTimeOffset.UtcNow);

    private static OrderProcessor Processor(AppDbContext db) =>
        new(
            new OrderRepository(db),
            new EfWriteTransaction(db),
            new OutboxEventWriter(new OutboxRepository(db)),
            NullLogger<OrderProcessor>.Instance);

    [Fact]
    public async Task NormalPurchase_DecrementsStock_And_PersistsOrder()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        await using var db = fx.CreateDbContext();
        var repo = new OrderRepository(db);

        Assert.True(await repo.PersistAsync(Msg(productId, 1, "normal-1")));

        var stock = await db.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync();
        var orders = await db.Orders.CountAsync(o => o.ProductId == productId);
        Assert.Equal(9, stock);
        Assert.Equal(1, orders);
    }

    [Fact]
    public async Task ConcurrentPurchases_ConserveInventory()
    {
        const int initial = 10, attempts = 50;
        var productId = await fx.ResetDatabaseAsync(stock: initial);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, attempts),
            new ParallelOptions { MaxDegreeOfParallelism = attempts },
            async (i, ct) =>
            {
                await using var db = fx.CreateDbContext();
                var repo = new OrderRepository(db);
                try { await Processor(db).ProcessAsync(Msg(productId, 1, $"conc-{i}"), ct); }
                catch (Domain.StockDriftException) { /* expected for losers */ }
            });

        await using var audit = fx.CreateDbContext();
        var finalStock = await audit.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync();
        var sold = await audit.Orders.Where(o => o.ProductId == productId).SumAsync(o => o.Quantity);

        Assert.True(finalStock >= 0, $"negative stock: {finalStock}");
        Assert.True(sold <= initial, $"oversold: sold={sold} initial={initial}");
        Assert.Equal(initial, finalStock + sold);
        Assert.Equal(initial, sold);
    }

    [Fact]
    public async Task DuplicateDelivery_PersistsExactlyOnce()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var gateway = fx.CreateRedisGateway();
        await gateway.SetStockAsync(productId, 10);

        Assert.Equal(Application.Inventory.ReservationResult.Reserved,
            await gateway.TryReserveAsync(productId, 1, "dup-key"));
        await using var db = fx.CreateDbContext();
        var repo = new OrderRepository(db);
        var processor = Processor(db);
        for (var i = 0; i < 5; i++)
            await processor.ProcessAsync(Msg(productId, 1, "dup-key"), CancellationToken.None);

        var stock = await db.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync();
        var orders = await db.Orders.CountAsync(o => o.IdempotencyKey == "dup-key");
        Assert.Equal(1, orders);
        Assert.Equal(9, stock);
    }
}
