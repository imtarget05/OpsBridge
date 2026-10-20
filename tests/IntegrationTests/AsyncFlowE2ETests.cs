using FlashSale.Application.Outbox;
using FlashSale.Application.Inventory;
using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSale.IntegrationTests;

/// <summary>
/// E2E-FS-03/04: những phần của flow mà OrderFlowTests chưa chạm tới.
///
/// OrderFlowTests đã chứng minh oversell và idempotency. Hai scenario ở đây
/// trả lời câu hỏi khó hơn: đi qua hàng đợi thật thì state còn đúng không, và
/// consumer chết giữa chừng thì có mất event không.
///
/// Chạy trên Postgres, Redis và RabbitMQ thật qua Testcontainers. Bỏ RabbitMQ
/// đi thì hai scenario này mất đúng ý nghĩa, nên chúng không mock.
/// </summary>
[Collection("flashsale")]
public sealed class AsyncFlowE2ETests(FlashSaleFixture fx)
{
    private static OrderMessage Msg(int productId, int qty, string key) =>
        new(productId, qty, key, DateTimeOffset.UtcNow);

    private static OrderProcessor Processor(AppDbContext db) =>
        new(
            new OrderRepository(db),
            new EfWriteTransaction(db),
            new OutboxEventWriter(new OutboxRepository(db)),
            NullLogger<OrderProcessor>.Instance);

    // ---------------------------------------------------------------- 03 ---
    [Fact]
    public async Task E2E_FS_03_Reserve_Queue_Consume_ConvergesToOneOrder()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var gateway = fx.CreateRedisGateway();
        await gateway.SetStockAsync(productId, 10);

        // Hop 1: reserve ở Redis. Chốt chặn này phải chạy TRƯỚC khi ghi order;
        // nếu chỉ chặn ở tầng DB thì mọi request thừa vẫn tới database.
        Assert.Equal(ReservationResult.Reserved,
            await gateway.TryReserveAsync(productId, 1, "e2e-03-key"));

        // Hop 2: publish lên hàng đợi thật.
        var queue = await fx.CreateRabbitQueueAsync("orders-e2e-03");
        var enqueued = await queue.EnqueueAsync(Msg(productId, 1, "e2e-03-key"));
        Assert.True(enqueued);

        // Hop 3: consumer đọc và ghi xuống Postgres.
        var entry = await queue.DequeueAsync();
        Assert.NotNull(entry);

        await using var db = fx.CreateDbContext();
        await Processor(db)
            .ProcessAsync(entry!.Message, CancellationToken.None);
        if (entry.Complete is not null) await entry.Complete();

        // Kết quả nghiệp vụ cuối.
        await using var audit = fx.CreateDbContext();
        var stock = await audit.Products.Where(p => p.Id == productId)
            .Select(p => p.AvailableStock).FirstAsync();
        var orders = await audit.Orders.CountAsync(o => o.ProductId == productId);
        var quantity = await audit.Orders.Where(o => o.ProductId == productId)
            .SumAsync(o => o.Quantity);

        Assert.Equal(9, stock);
        Assert.Equal(1, orders);
        Assert.Equal(10, stock + quantity);
    }

    // ---------------------------------------------------------------- 04 ---
    [Fact]
    public async Task E2E_FS_04_ConsumerDies_ThenRecovers_WithoutDuplicating()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var gateway = fx.CreateRedisGateway();
        await gateway.SetStockAsync(productId, 10);
        await gateway.TryReserveAsync(productId, 1, "e2e-04-key");

        var queue = await fx.CreateRabbitQueueAsync("orders-e2e-04");
        await queue.EnqueueAsync(Msg(productId, 1, "e2e-04-key"));

        // Consumer "chết": nhận message rồi không xử lý, không ack — đúng
        // như một process bị kill sau khi đã lấy message khỏi hàng đợi.
        var stranded = await queue.DequeueAsync();
        Assert.NotNull(stranded);

        // Chưa gì được ghi: state phải VẪN NGUYÊN. Phần này hay bị bỏ sót —
        // nhiều hệ thống "sửa" bằng cách tự chèn một order giả cho khớp số.
        await using (var midway = fx.CreateDbContext())
        {
            Assert.Equal(0, await midway.Orders.CountAsync(o => o.ProductId == productId));
            Assert.Equal(10, await midway.Products
                .Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync());
        }

        // Consumer mới xử lý lại message đó (at-least-once).
        await using (var db = fx.CreateDbContext())
            await Processor(db)
                .ProcessAsync(stranded!.Message, CancellationToken.None);
        if (stranded.Complete is not null) await stranded.Complete();

        await using var audit = fx.CreateDbContext();
        var stock = await audit.Products.Where(p => p.Id == productId)
            .Select(p => p.AvailableStock).FirstAsync();
        var quantity = await audit.Orders.Where(o => o.ProductId == productId)
            .SumAsync(o => o.Quantity);
        var orders = await audit.Orders.CountAsync(o => o.ProductId == productId);

        Assert.Equal(9, stock);
        Assert.Equal(1, orders);
        Assert.Equal(10, stock + quantity);
    }
}
