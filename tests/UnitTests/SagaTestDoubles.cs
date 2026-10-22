using FlashSale.Application.Events;
using FlashSale.Application.Inventory;
using FlashSale.Application.Payment;
using FlashSale.Application.Persistence;
using FlashSale.Application.Saga;
using FlashSale.Domain;
using FlashSale.Domain.Events;
using FlashSale.Domain.Saga;

namespace FlashSale.UnitTests;

internal sealed class FakeCheckoutSagaRepository : ICheckoutSagaRepository
{
    private readonly Dictionary<Guid, CheckoutSagaState> _bySagaId = new();
    private readonly Dictionary<string, CheckoutSagaState> _byKey = new();
    private readonly Dictionary<int, CheckoutSagaState> _byOrderId = new();

    public Task<CheckoutSagaState?> GetBySagaIdAsync(Guid sagaId, CancellationToken ct = default) =>
        Task.FromResult(_bySagaId.TryGetValue(sagaId, out var s) ? s : null);

    public Task<CheckoutSagaState?> GetByOrderIdAsync(int orderId, CancellationToken ct = default) =>
        Task.FromResult(_byOrderId.TryGetValue(orderId, out var s) ? s : null);

    public Task<CheckoutSagaState?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(_byKey.TryGetValue(idempotencyKey, out var s) ? s : null);

    public Task SaveAsync(CheckoutSagaState saga, CancellationToken ct = default)
    {
        if (_bySagaId.TryGetValue(saga.SagaId, out var existing))
        {
            if (existing.Version != saga.Version)
            {
                throw new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException(
                    $"Optimistic concurrency violation: CheckoutSaga {saga.SagaId} presented version {saga.Version} but repository version is {existing.Version}.");
            }

            existing.OrderId = saga.OrderId;
            existing.Status = saga.Status;
            existing.InventoryStatus = saga.InventoryStatus;
            existing.PaymentStatus = saga.PaymentStatus;
            existing.FailureReason = saga.FailureReason;
            existing.CompensationReason = saga.CompensationReason;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.Version++;
            saga.Version = existing.Version;
        }
        else
        {
            _bySagaId[saga.SagaId] = saga;
            _byKey[saga.IdempotencyKey] = saga;
            if (saga.OrderId > 0)
            {
                _byOrderId[saga.OrderId] = saga;
            }
        }
        return Task.CompletedTask;
    }
}

internal sealed class FakeStockReservationGateway : IStockReservationGateway
{
    public ReservationResult NextResult { get; set; } = ReservationResult.Reserved;
    public int TryReserveCalls { get; private set; }
    public int ReleaseCalls { get; private set; }
    public readonly List<(int ProductId, int Quantity, string Key)> ReleasedReservations = new();

    public Task<ReservationResult> TryReserveAsync(int productId, int quantity, string idempotencyKey)
    {
        TryReserveCalls++;
        return Task.FromResult(NextResult);
    }

    public Task ReleaseReservationAsync(int productId, int quantity, string idempotencyKey)
    {
        ReleaseCalls++;
        ReleasedReservations.Add((productId, quantity, idempotencyKey));
        return Task.CompletedTask;
    }

    public Task SetStockAsync(int productId, int stock) => Task.CompletedTask;
    public Task<int?> GetCachedStockAsync(int productId) => Task.FromResult<int?>(100);
}

internal sealed class FakePaymentRepository : IPaymentRepository
{
    public int MarkPaidCalls { get; private set; }
    public int CancelAndReleaseCalls { get; private set; }
    public readonly List<int> CancelledOrders = new();

    public Task<bool> CancelAndReleaseStockAsync(int orderId, CancellationToken ct = default)
    {
        CancelAndReleaseCalls++;
        CancelledOrders.Add(orderId);
        return Task.FromResult(true);
    }

    public Task<bool> MarkPaidAsync(int orderId, CancellationToken ct = default)
    {
        MarkPaidCalls++;
        return Task.FromResult(true);
    }

    public Task<bool> MarkPaymentFailedAsync(int orderId, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<int> IncrementReminderAsync(int orderId, CancellationToken ct = default) =>
        Task.FromResult(1);

    public Task<PaymentOrderView?> GetByKeyAsync(string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult<PaymentOrderView?>(null);
}

internal sealed class FakePaymentClient : IPaymentClient
{
    public Func<PaymentClientRequest, PaymentClientResult>? Handler { get; set; }
    public int Calls { get; private set; }

    public Task<PaymentClientResult> ProcessPaymentAsync(PaymentClientRequest request, CancellationToken ct = default)
    {
        Calls++;
        if (Handler is not null)
        {
            return Task.FromResult(Handler(request));
        }

        var cents = (int)Math.Round((request.Amount - Math.Floor(request.Amount)) * 100);
        if (cents == 2)
        {
            return Task.FromResult(new PaymentClientResult(false, "Declined", null, "Card declined: insufficient funds", false));
        }
        if (cents == 3)
        {
            return Task.FromResult(new PaymentClientResult(false, "TimedOut", null, "Gateway timed out", true));
        }

        return Task.FromResult(new PaymentClientResult(true, "Succeeded", $"tx_{Guid.NewGuid():N}", null, false));
    }
}

internal sealed class FakeDomainEventPublisher : IDomainEventPublisher
{
    public readonly List<DomainEvent> PublishedEvents = new();

    public Task PublishAsync(DomainEvent @event, CancellationToken ct = default)
    {
        PublishedEvents.Add(@event);
        return Task.CompletedTask;
    }
}
