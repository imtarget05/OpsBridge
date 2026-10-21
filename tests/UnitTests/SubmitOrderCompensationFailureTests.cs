using FlashSale.Application.Automation;
using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Application.Outbox;
using FlashSale.Application.Persistence;
using FlashSale.Domain.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FlashSale.UnitTests;

public class SubmitOrderCompensationFailureTests
{
    private sealed class RejectingQueueProducer : IOrderQueueProducer
    {
        public ValueTask<bool> EnqueueAsync(OrderMessage message, CancellationToken ct = default) =>
            ValueTask.FromResult(false); // always reject to trigger backpressure/compensation
    }

    private sealed class FakeReadModel : IOrderReadModel
    {
        public Task<int?> GetStockAsync(int productId, CancellationToken ct = default) => Task.FromResult<int?>(10);
        public Task<ProductView?> GetProductAsync(int productId, CancellationToken ct = default) =>
            Task.FromResult<ProductView?>(new ProductView(productId, "Test", 10, 9.99m, string.Empty));
        public Task<OrderStatusView?> GetOrderStatusAsync(string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult<OrderStatusView?>(null);
        public Task<IReadOnlyList<OrderSummaryView>> GetOrdersByUserAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrderSummaryView>>([]);
        public Task<IReadOnlyList<PendingPaymentView>> GetPendingPaymentOrdersAsync(int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PendingPaymentView>>([]);
    }

    [Fact]
    public async Task WhenQueueRejectsAndCompensationOutboxFails_RedisIsReleased_AndReconcilerCanRepair()
    {
        var gateway = new FakeStockReservationGateway();
        var outboxRepo = new FakeOutboxRepository();
        var outboxWriter = new OutboxEventWriter(outboxRepo);
        var transaction = new FakeWriteTransaction();
        var readModel = new FakeReadModel();
        var queue = new RejectingQueueProducer();
        var processor = new OrderProcessor(
            new FakeOrderRepository(),
            transaction,
            outboxWriter,
            NullLogger<OrderProcessor>.Instance);

        var useCase = new SubmitOrderUseCase(
            gateway,
            queue,
            readModel,
            processor,
            transaction,
            outboxWriter,
            new PaymentAutomationOptions(),
            NullLogger<SubmitOrderUseCase>.Instance);

        const int productId = 101;
        const int quantity = 2;
        const string key = "test-compensation-failure-key";
        var correlationId = Guid.NewGuid();

        // 1. Simulate failure specifically on inventory.released write
        outboxRepo.EnqueueFailureFactory = msg => msg.EventType == "inventory.released"
            ? new InvalidOperationException("Simulated transient database failure during inventory.released outbox write.")
            : null;

        var result = await useCase.ExecuteAsync(productId, quantity, key, correlationId, CancellationToken.None);

        // Caller must receive SystemBusy (503) without unhandled crash
        Assert.Equal(SubmitOrderOutcome.SystemBusy, result.Outcome);

        // Redis reservation was promptly released
        Assert.Equal(1, gateway.ReleaseCalls);
        Assert.Contains(gateway.ReleasedReservations, r => r.ProductId == productId && r.Quantity == quantity && r.Key == key);

        // Outbox contains inventory.reserved, but NO inventory.released yet (failure window reproduced)
        Assert.Contains(outboxRepo.Enqueued, m => m.EventType == "inventory.reserved");
        Assert.DoesNotContain(outboxRepo.Enqueued, m => m.EventType == "inventory.released");

        // 2. Reconciler runs when database is recovered:
        outboxRepo.EnqueueFailureFactory = null;

        bool repaired = await useCase.ReconcileOrphanReservationAsync(productId, quantity, key, correlationId, CancellationToken.None);

        Assert.True(repaired, "Reconciler must successfully record compensation outbox event once database is healthy");
        Assert.Contains(outboxRepo.Enqueued, m => m.EventType == "inventory.released");
    }
}
