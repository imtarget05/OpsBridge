using FlashSale.Application.Auth;
using FlashSale.Application.Automation;
using FlashSale.Application.Events;
using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Domain;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Events;
using FlashSale.Infrastructure.Events;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Spec §16 E2E core: create order → payment timeout → cancellation →
/// stock release → automation audit record. Real Postgres/Redis, guarded
/// transitions, event capture through the in-memory publisher.
/// </summary>
[Collection("flashsale")]
public sealed class PaymentAutomationTests(FlashSaleFixture fx)
{
    private static OrderMessage Msg(int productId, int qty, string key, DateTimeOffset? dueAt) =>
        new(productId, qty, key, DateTimeOffset.UtcNow, PaymentDueAt: dueAt,
            CorrelationId: Guid.NewGuid());

    private static PaymentAutomationOptions Options(int grace = 0, int maxReminders = 3) =>
        new() { TimeoutMinutes = 0, GracePeriodMinutes = grace, MaxPaymentReminders = maxReminders };

    private async Task<int> CreatePendingOrderAsync(int productId, string key)
    {
        await using var db = fx.CreateDbContext();
        var repo = new OrderRepository(db);
        await fx.CreateOrderProcessor(db)
            .ProcessAsync(Msg(productId, 1, key, DateTimeOffset.UtcNow.AddMinutes(-10)),
                CancellationToken.None);
        return await repo.GetOrderIdAsync(key) ?? throw new InvalidOperationException("no order");
    }

    private async Task<int> CurrentStockAsync(int productId)
    {
        await using var db = fx.CreateDbContext();
        return await db.Products.Where(p => p.Id == productId)
            .Select(p => p.AvailableStock).FirstAsync();
    }

    [Fact]
    public async Task Persist_WithPaymentWindow_CreatesPendingPaymentOrder()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        await using var db = fx.CreateDbContext();
        var repo = new OrderRepository(db);

        await fx.CreateOrderProcessor(db)
            .ProcessAsync(Msg(productId, 2, "pay-window-1", DateTimeOffset.UtcNow.AddMinutes(15)),
                CancellationToken.None);

        var order = await db.Orders.SingleAsync(o => o.IdempotencyKey == "pay-window-1");
        Assert.Equal(OrderStatus.PendingPayment, order.Status);
        Assert.NotNull(order.PaymentDueAt);
        Assert.NotNull(order.CorrelationId);
        // stock already decremented at persist (reservation semantics unchanged)
        Assert.Equal(8, await db.Products.Where(p => p.Id == productId)
            .Select(p => p.AvailableStock).FirstAsync());
    }

    [Fact]
    public async Task TimeoutScan_PastGrace_Cancels_ReleasesStock_PublishesEvents_WritesAudit()
    {
        var initial = 10;
        var productId = await fx.ResetDatabaseAsync(stock: initial);
        var gateway = fx.CreateRedisGateway();
        await gateway.SetStockAsync(productId, initial);

        var orderId = await CreatePendingOrderAsync(productId, "timeout-1");
        Assert.Equal(initial - 1, await CurrentStockAsync(productId)); // decrement persisted

        // Mirror the production sequence exactly: the API reserved stock in Redis
        // BEFORE the worker persisted (10 → 9). Without this the cancel-release
        // (+1) would overshoot, because persist itself never touches Redis.
        Assert.Equal(Application.Inventory.ReservationResult.Reserved,
            await gateway.TryReserveAsync(productId, 1, "timeout-1"));
        Assert.Equal(initial - 1, await gateway.GetCachedStockAsync(productId));

        await using (var db = fx.CreateDbContext())
        {
            var useCase = fx.CreatePaymentAutomationUseCase(db, gateway, Options(grace: 0));

            var result = await useCase.ExecuteAsync("manual", CancellationToken.None);

            Assert.Equal(1, result.Scanned);
            Assert.Equal(0, result.Reminded);
            Assert.Equal(1, result.Cancelled);
        }

        await using (var verify = fx.CreateDbContext())
        {
            var order = await verify.Orders.SingleAsync(o => o.Id == orderId);
            Assert.Equal(OrderStatus.Cancelled, order.Status);
            Assert.NotNull(order.PaymentProcessedAt);
            Assert.Equal("expired", order.LastPaymentResult);

            Assert.Equal(initial, await verify.Products
                .Where(p => p.Id == productId).Select(p => p.AvailableStock).FirstAsync());

            var run = await verify.AutomationRuns
                .Where(r => r.WorkflowName == AutomationWorkflow.PaymentTimeout.ToString())
                .OrderByDescending(r => r.Id).FirstAsync();
            Assert.Equal(AutomationRunStatus.Success, run.Status);
            Assert.Equal("manual", run.TriggerType);
            Assert.Contains("cancelled=1", run.ResultSummary);
            Assert.NotNull(run.FinishedAt);
        }

        Assert.Equal(initial, await gateway.GetCachedStockAsync(productId)); // cache mirrored

        // ADR-019: the three events are NOT published directly any more — they
        // are written to the transactional outbox in the same transaction as
        // the cancellation.
        await using (var outboxDb = fx.CreateDbContext())
        {
            var outboxTypes = await outboxDb.OutboxMessages.Select(m => m.EventType).ToListAsync();
            Assert.Contains("payment.expired", outboxTypes);
            Assert.Contains("order.cancelled", outboxTypes);
            Assert.Contains("inventory.released", outboxTypes);
            // Exactly ONE event of each kind — the scan must not double-publish.
            Assert.Equal(3, await outboxDb.OutboxMessages.CountAsync(m =>
                m.EventType == "payment.expired"
                || m.EventType == "order.cancelled"
                || m.EventType == "inventory.released"));
            // Plus the order.created event the order processor wrote.
            Assert.Equal(4, await outboxDb.OutboxMessages.CountAsync());
            Assert.Contains("order.created", outboxTypes);
        }
    }

    [Fact]
    public async Task TimeoutScan_IsIdempotent_SecondRunCancelsNothing()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var gateway = fx.CreateRedisGateway();
        await gateway.SetStockAsync(productId, 10);
        var orderId = await CreatePendingOrderAsync(productId, "timeout-idem");

        await using var db = fx.CreateDbContext();
        var useCase = fx.CreatePaymentAutomationUseCase(db, gateway, Options(grace: 0));

        var first = await useCase.ExecuteAsync("manual", CancellationToken.None);
        var stockAfterFirst = await CurrentStockAsync(productId);

        var second = await useCase.ExecuteAsync("timer", CancellationToken.None);

        Assert.Equal(1, first.Cancelled);
        Assert.Equal(0, second.Scanned);   // no longer pending — filtered out
        Assert.Equal(0, second.Cancelled); // no double release
        Assert.Equal(stockAfterFirst, await CurrentStockAsync(productId));
        Assert.Equal(10, stockAfterFirst);
    }

    [Fact]
    public async Task RecordPayment_Completed_ConfirmsOnce_DuplicateIsNoTransition()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var key = "pay-ok-1";
        var orderId = await CreatePendingOrderAsync(productId, key);

        await using var db = fx.CreateDbContext();
        var useCase = fx.CreateRecordPaymentUseCase(db);

        // These are use-case tests, not HTTP tests: the caller is the platform
        // itself (an operator runbook / the timer), so the identity is explicit
        // rather than implied. The HTTP ownership path is covered in
        // EndpointAuthorizationTests.
        var first = await useCase.ExecuteAsync(key, PaymentOutcome.Completed, CallerIdentity.Operator, CancellationToken.None);
        var second = await useCase.ExecuteAsync(key, PaymentOutcome.Completed, CallerIdentity.Operator, CancellationToken.None);

        Assert.True(first.Found);
        Assert.True(first.Transitioned);
        Assert.True(second.Found);
        Assert.False(second.Transitioned); // guarded: no double-confirm

        var order = await db.Orders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        // ADR-019: the revenue events are written to the OUTBOX in the same
        // transaction as the status change — they are no longer published
        // directly to the broker.
        var outboxTypes = await db.OutboxMessages.Select(m => m.EventType).ToListAsync();
        Assert.Contains("payment.completed", outboxTypes);
        Assert.Contains("order.confirmed", outboxTypes);
        // Stock was consumed by the order and stays consumed (paid => no release)
        Assert.Equal(9, await db.Products.Where(p => p.Id == productId)
            .Select(p => p.AvailableStock).FirstAsync());
    }

    [Fact]
    public async Task RecordPayment_Failed_KeepsOrderPending()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var key = "pay-fail-1";
        var orderId = await CreatePendingOrderAsync(productId, key);

        await using var db = fx.CreateDbContext();
        var useCase = fx.CreateRecordPaymentUseCase(db);

        var result = await useCase.ExecuteAsync(key, PaymentOutcome.Failed, CallerIdentity.Operator, CancellationToken.None);

        Assert.True(result.Transitioned);
        var order = await db.Orders.SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.PendingPayment, order.Status); // still awaiting payment
        Assert.Equal("failed", order.LastPaymentResult);
        var outboxTypes = await db.OutboxMessages.Select(m => m.EventType).ToListAsync();
        Assert.Contains("payment.failed", outboxTypes);
        // Still eligible for the timeout scan afterwards; stock untouched
        Assert.Equal(9, await db.Products.Where(p => p.Id == productId)
            .Select(p => p.AvailableStock).FirstAsync());
    }
}
