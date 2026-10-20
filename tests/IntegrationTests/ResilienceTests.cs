using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Infrastructure.Messaging;
using FlashSale.Infrastructure.Persistence;
using FlashSale.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSale.IntegrationTests;

[Collection("flashsale")]
public sealed class ResilienceTests(FlashSaleFixture fx)
{
    private static OrderMessage Msg(int productId, int qty, string key) =>
        new(productId, qty, key, DateTimeOffset.UtcNow);

    [Fact]
    public async Task RedisDown_FallsBack_To_SynchronousPersist()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        // abortConnect=false: Connect succeeds lazily; operations then fail
        // and the gateway reports Unavailable (the production fallback path).
        using var mux = StackExchange.Redis.ConnectionMultiplexer.Connect(
            "localhost:6399,abortConnect=false,connectTimeout=500,connectRetry=0,syncTimeout=500");
        var deadGateway = new RedisStockGateway(mux);

        var reservation = await deadGateway.TryReserveAsync(productId, 1, "fallback-1");
        Assert.Equal(Application.Inventory.ReservationResult.Unavailable, reservation);

        await using var db = fx.CreateDbContext();
        var repo = new OrderRepository(db);
        await fx.CreateOrderProcessor(db)
            .ProcessAsync(Msg(productId, 1, "fallback-1"), CancellationToken.None);

        var stock = await db.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync();
        Assert.Equal(9, stock);
    }

    [Fact]
    public async Task RabbitMq_RoundTrip_PersistsOrder_And_Acks()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        using var queue = await fx.CreateRabbitQueueAsync($"orders-{Guid.NewGuid():N}");

        Assert.True(await queue.EnqueueAsync(Msg(productId, 2, "rmq-1")));
        var entry = await queue.DequeueAsync();
        Assert.NotNull(entry);

        await using var db = fx.CreateDbContext();
        await fx.CreateOrderProcessor(db)
            .ProcessAsync(entry!.Message, CancellationToken.None);
        await entry.Complete!();

        var stock = await db.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync();
        Assert.Equal(8, stock);
        Assert.Equal(1, await db.Orders.CountAsync(o => o.IdempotencyKey == "rmq-1"));
    }

    [Fact]
    public async Task InMemoryQueue_Full_ReturnsFalse_BackpressureSignal()
    {
        var q = new InMemoryOrderQueue();
        var ok = true;
        for (var i = 0; i < 5_005; i++)
            ok = await q.EnqueueAsync(Msg(1, 1, $"fill-{i}"));
        Assert.False(ok);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task InvalidQuantity_PersistsNothing_And_StockUnchanged(int qty)
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        await using var db = fx.CreateDbContext();
        var repo = new OrderRepository(db);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repo.PersistAsync(Msg(productId, qty, $"bad-{qty}"), CancellationToken.None));

        Assert.Equal(10, await db.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync());
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task UnknownProduct_PersistsNothing()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        await using var db = fx.CreateDbContext();
        var repo = new OrderRepository(db);

        Assert.False(await repo.PersistAsync(Msg(productId + 9999, 1, "ghost-1")));
        Assert.Equal(10, await db.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync());
        Assert.Equal(0, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task RestartSimulation_KeepsSchema_Orders_And_Stock()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        await using (var db = fx.CreateDbContext())
            Assert.True(await new OrderRepository(db)
                .PersistAsync(Msg(productId, 3, "restart-1")));

        await using var fresh = fx.CreateDbContext();
        Assert.Equal(7, await fresh.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync());
        Assert.Equal(1, await fresh.Orders.CountAsync(o => o.IdempotencyKey == "restart-1"));
        Assert.Equal(1, await fresh.Products.CountAsync());
    }
}
