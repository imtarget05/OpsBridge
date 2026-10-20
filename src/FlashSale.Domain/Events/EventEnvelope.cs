using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashSale.Domain.Events;

/// <summary>
/// Canonical event envelope (ADR-019).
///
/// Every event that leaves the system — no matter which write path produced it —
/// is wrapped in this envelope, so a consumer can rely on ONE shape:
///
/// <code>
/// {
///   "event_id":        "b7c1…",   // UUID, STABLE: generated once when the outbox
///                                   // row is written, reused for every retry.
///   "event_type":      "order.created",
///   "aggregate_type":  "order",
///   "aggregate_id":    "42",
///   "occurred_at":     "2026-10-20T10:00:00+00:00",
///   "correlation_id":  "9f2c…",   // follows the request → event → audit trail.
///   "schema_version":  1,
///   "payload":         { …typed event… }   // JSON text of the typed event
/// }
/// </code>
///
/// <para>
/// <c>event_id</c> is the consumer-side dedup key: it is written into the
/// outbox row (as <c>OutboxMessages.MessageId</c>) together with the envelope,
/// so a redelivery after a crash carries the SAME id and the inbox dedup can
/// recognise it.
/// </para>
/// </summary>
public sealed record EventEnvelope
{
    /// <summary>Version of this envelope contract. Bump on breaking changes.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("event_id")]
    public required string EventId { get; init; }

    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    [JsonPropertyName("aggregate_type")]
    public required string AggregateType { get; init; }

    [JsonPropertyName("aggregate_id")]
    public required string AggregateId { get; init; }

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("correlation_id")]
    public Guid CorrelationId { get; init; }

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The typed event, serialized as JSON text.</summary>
    [JsonPropertyName("payload")]
    public required string Payload { get; init; }

    /// <summary>Serialize the envelope to its canonical wire format.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Parse an envelope. Returns <c>null</c> for anything that is not a
    /// well-formed envelope — a poison payload must be detectable, not fatal.
    /// </summary>
    public static EventEnvelope? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope>(json, JsonOptions);
            if (envelope is null
                || string.IsNullOrWhiteSpace(envelope.EventId)
                || string.IsNullOrWhiteSpace(envelope.EventType)
                || string.IsNullOrWhiteSpace(envelope.Payload))
            {
                return null;
            }

            return envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Build the envelope for a typed domain event. The event id is the stable
    /// id of the event — it is generated once here and reused for every retry
    /// of the same outbox row.
    /// </summary>
    public static EventEnvelope FromEvent(
        DomainEvent @event,
        string aggregateType,
        string aggregateId)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return new EventEnvelope
        {
            EventId = @event.EventId,
            EventType = @event.EventType,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            OccurredAt = @event.OccurredAt,
            CorrelationId = @event.CorrelationId,
            SchemaVersion = CurrentSchemaVersion,
            Payload = JsonSerializer.Serialize(@event, @event.GetType())
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}

/// <summary>
/// Implemented by domain events that travel with a canonical envelope
/// (ADR-019). The transport serializes the ENVELOPE — not the wrapper — so
/// the wire format is identical no matter which path produced the event.
/// </summary>
public interface ICarriesEventEnvelope
{
    EventEnvelope Envelope { get; }
}
