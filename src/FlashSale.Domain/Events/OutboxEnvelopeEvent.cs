namespace FlashSale.Domain.Events;

/// <summary>
/// A domain event that travels with its canonical envelope (ADR-019).
///
/// <para>
/// The transport serializes the ENVELOPE, not this wrapper, so the wire
/// format is the same for every producer. The wrapper exists only to carry the
/// envelope through the <c>IDomainEventPublisher</c> port, which speaks
/// <see cref="DomainEvent"/>.
/// </para>
/// </summary>
public sealed record OutboxEnvelopeEvent(EventEnvelope Envelope)
    : DomainEvent(
        Envelope.EventId,
        Envelope.EventType,
        Envelope.OccurredAt,
        Envelope.CorrelationId,
        "outbox-dispatcher"),
      ICarriesEventEnvelope
{
    public override object Payload => Envelope.Payload;
}
