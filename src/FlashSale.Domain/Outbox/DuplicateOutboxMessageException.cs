namespace FlashSale.Domain.Outbox;

/// <summary>
/// An outbox row with the same <c>MessageId</c> already exists (ADR-019).
///
/// <para>
/// The outbox is keyed by the event id: <c>OutboxMessages.MessageId</c> carries
/// the STABLE <c>event_id</c> that the consumer dedups on, and the database
/// enforces it with a unique index. Two rows with the same id would therefore
/// mean two different payloads sharing one dedup key — the consumer would
/// apply the first and silently drop the second.
/// </para>
///
/// <para>
/// This is a DOMAIN condition, not a server fault: the caller asked for a row
/// that is already there. It is translated from the database's unique
/// violation at the repository boundary so the transport can answer
/// <c>409 Conflict</c> instead of leaking an unhandled <c>23505</c> as a
/// <c>500</c>.
/// </para>
/// </summary>
public sealed class DuplicateOutboxMessageException(Guid messageId)
    : Exception($"An outbox message with id '{messageId}' already exists.")
{
    /// <summary>The event id that was already taken.</summary>
    public Guid MessageId { get; } = messageId;
}
