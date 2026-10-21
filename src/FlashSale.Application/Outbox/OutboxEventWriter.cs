using FlashSale.Domain.Events;
using FlashSale.Domain.Outbox;

namespace FlashSale.Application.Outbox;

/// <summary>
/// Writes domain events into the transactional outbox (ADR-019).
///
/// <para>
/// This is the ONLY sanctioned way for a business write path to emit an event.
/// It never touches the broker: the row is written inside the caller's write
/// transaction, so the event and the state change commit together or not at
/// all. Publication is the dispatcher's job.
/// </para>
/// </summary>
public sealed class OutboxEventWriter(IOutboxRepository outboxRepository)
{
    /// <summary>Exchange the dispatcher publishes to (topic exchange).</summary>
    public const string EventExchange = "automation.events";

    /// <summary>Aggregate type of an order.</summary>
    public const string OrderAggregate = "order";

    /// <summary>Aggregate type of a checkout saga.</summary>
    public const string SagaAggregate = "checkout-saga";

    /// <summary>Aggregate type of an inventory/stock alert.</summary>
    public const string InventoryAggregate = "inventory";

    /// <summary>
    /// Enqueue an event. MUST be called inside the business write
    /// transaction — otherwise the row commits on its own and the dual-write
    /// gap comes back.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The event id is not a UUID — the outbox row is keyed by it and the
    /// consumer dedups on it.
    /// </exception>
    public async Task EnqueueAsync(
        DomainEvent @event,
        string aggregateType,
        string aggregateId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (!Guid.TryParse(@event.EventId, out var messageId))
        {
            throw new ArgumentException(
                $"Event id '{@event.EventId}' is not a UUID — the outbox row and the " +
                "consumer dedup key are both keyed by it.",
                nameof(@event));
        }

        var envelope = EventEnvelope.FromEvent(@event, aggregateType, aggregateId);

        await outboxRepository.EnqueueAsync(new OutboxMessage
        {
            MessageId = messageId,
            EventType = envelope.EventType,
            Topic = EventExchange,
            Payload = envelope.ToJson(),
            AggregateType = envelope.AggregateType,
            AggregateId = envelope.AggregateId,
            CorrelationId = envelope.CorrelationId,
            SchemaVersion = envelope.SchemaVersion,
            CreatedAt = envelope.OccurredAt
        }, ct);
    }
}
