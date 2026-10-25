namespace FlashSale.Domain.Events;

/// <summary>Base for all automation events (spec §1).</summary>
public abstract record DomainEvent(
    string EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    string Source)
{
    public abstract object Payload { get; }
}

/// <summary>Order lifecycle: customer creates order (spec §4).</summary>
public sealed record OrderCreatedEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int OrderId,
    int ProductId,
    int Quantity,
    Guid? UserId,
    string IdempotencyKey,
    OrderStatus Status) : DomainEvent(EventId, "order.created", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { OrderId, ProductId, Quantity, UserId, IdempotencyKey, Status };
}

/// <summary>Stock reserved in Redis for an order (spec §4).</summary>
public sealed record InventoryReservedEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int OrderId,
    int ProductId,
    int Quantity,
    string IdempotencyKey) : DomainEvent(EventId, "inventory.reserved", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { OrderId, ProductId, Quantity, IdempotencyKey };
}

/// <summary>
/// The accepted reservation has not produced an Order within its configured
/// queue-to-persistence deadline. The event identifies the reservation, so
/// repeated scans never create a second alert for the same accepted attempt.
/// </summary>
public sealed record OrderFulfillmentStuckEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    string IdempotencyKey,
    Guid ReservationEventId,
    DateTimeOffset AcceptedAt,
    int ThresholdMinutes) : DomainEvent(EventId, "order.fulfillment_stuck", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { IdempotencyKey, ReservationEventId, AcceptedAt, ThresholdMinutes };
}

/// <summary>Payment completed for an order (spec §4).</summary>
public sealed record PaymentCompletedEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int OrderId,
    decimal Amount,
    string PaymentMethod) : DomainEvent(EventId, "payment.completed", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { OrderId, Amount, PaymentMethod };
}

/// <summary>Payment failed for an order (spec §4).</summary>
public sealed record PaymentFailedEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int OrderId,
    string Reason) : DomainEvent(EventId, "payment.failed", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { OrderId, Reason };
}

/// <summary>Payment expired (timeout) for an order (spec §4/§5).</summary>
public sealed record PaymentExpiredEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int OrderId,
    int TimeoutMinutes) : DomainEvent(EventId, "payment.expired", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { OrderId, TimeoutMinutes };
}

/// <summary>Order confirmed after payment (spec §4).</summary>
public sealed record OrderConfirmedEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int OrderId) : DomainEvent(EventId, "order.confirmed", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { OrderId };
}

/// <summary>Order cancelled (spec §4/§5).</summary>
public sealed record OrderCancelledEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int OrderId,
    string Reason) : DomainEvent(EventId, "order.cancelled", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { OrderId, Reason };
}

/// <summary>Stock released after cancellation (spec §4/§5).</summary>
public sealed record InventoryReleasedEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int OrderId,
    int ProductId,
    int Quantity) : DomainEvent(EventId, "inventory.released", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { OrderId, ProductId, Quantity };
}

/// <summary>
/// Durable state: the compensation of a committed <c>inventory.reserved</c> event
/// failed after bounded retries (transient DB outage). Written to the outbox
/// inside the same transaction as the failed <c>inventory.released</c> attempt.
/// A reconciling process reads this event to repair the orphan reservation by
/// re-emitting <c>inventory.released</c> exactly once.
/// </summary>
public sealed record InventoryCompensationFailedEvent(
    string EventId,
    string IdempotencyKey,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int ProductId,
    int Quantity) : DomainEvent(EventId, "inventory.compensation_failed", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { IdempotencyKey, ProductId, Quantity };
}

/// <summary>Low-stock alert raised (spec §6).</summary>
public sealed record InventoryLowStockEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    int ProductId,
    int AvailableStock,
    int ReorderThreshold) : DomainEvent(EventId, "inventory.low_stock", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { ProductId, AvailableStock, ReorderThreshold };
}

/// <summary>Checkout Saga started (Phase 11).</summary>
public sealed record CheckoutSagaStartedEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    Guid SagaId,
    int OrderId,
    int ProductId,
    int Quantity,
    decimal Amount,
    string IdempotencyKey) : DomainEvent(EventId, "checkout.saga.started", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { SagaId, OrderId, ProductId, Quantity, Amount, IdempotencyKey };
}

/// <summary>Checkout Saga completed successfully (Phase 11).</summary>
public sealed record CheckoutSagaCompletedEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    Guid SagaId,
    int OrderId,
    string TransactionId) : DomainEvent(EventId, "checkout.saga.completed", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { SagaId, OrderId, TransactionId };
}

/// <summary>Checkout Saga compensated after failure (Phase 11).</summary>
public sealed record CheckoutSagaCompensatedEvent(
    string EventId,
    Guid CorrelationId,
    string Source,
    DateTimeOffset OccurredAt,
    Guid SagaId,
    int OrderId,
    string Reason) : DomainEvent(EventId, "checkout.saga.compensated", OccurredAt, CorrelationId, Source)
{
    public override object Payload => new { SagaId, OrderId, Reason };
}
