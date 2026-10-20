namespace FlashSale.Domain.Outbox;

/// <summary>
/// Transactional Outbox pattern entity (Phase 10).
/// Guarantees at-least-once messaging by committing event rows in the SAME
/// local ACID transaction as business state mutations.
/// </summary>
public class OutboxMessage
{
    /// <summary>
    /// Attempts made before a row is dead-lettered. 5 attempts with the
    /// exponential backoff below means a transient outage shorter than
    /// ~30 s (2+4+8+16) drains by itself once the broker returns.
    /// </summary>
    public const int MaxRetryCount = 5;

    /// <summary>Backoff ceiling, so a long outage does not push a retry out by hours.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    public long Id { get; set; }

    /// <summary>
    /// STABLE event id (ADR-019). Generated ONCE when the row is written and
    /// reused for every publish retry, so the consumer can dedup redeliveries
    /// on it. It is also the <c>event_id</c> inside the stored envelope.
    /// </summary>
    public Guid MessageId { get; set; } = Guid.NewGuid();

    public string EventType { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;

    /// <summary>
    /// The canonical event envelope as JSON text (ADR-019). Stored, not
    /// re-derived at publish time, so a retry republishes byte-identical
    /// content — including the stable <c>event_id</c>.
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Aggregate the event is about ("order", "payment", "saga").</summary>
    public string? AggregateType { get; set; }

    /// <summary>Identity of the aggregate the event is about ("42", "…").</summary>
    public string? AggregateId { get; set; }

    /// <summary>Correlation id that ties the event to its request/audit trail.</summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>Version of the envelope contract the payload was written with.</summary>
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Earliest moment the dispatcher may retry this row. NULL means
    /// "eligible now" (never attempted, or just requeued).
    /// Without this the dispatcher burned all 5 retries inside one second
    /// during an outage and the row became permanently invisible: pending=0.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>
    /// Terminal marker: retries exhausted, row is no longer eligible for the
    /// normal dispatcher and must be surfaced as "stuck" (then requeued by an
    /// operator or a DLQ drain). Non-null is the ONLY reliable "needs a human"
    /// signal — RetryCount alone is ambiguous once a row is requeued.
    /// </summary>
    public DateTimeOffset? DeadLetteredAt { get; set; }

    /// <summary>
    /// Identifies the dispatcher that currently owns the next delivery attempt.
    /// The token is cleared by success, failure, expiry, or operator requeue.
    /// </summary>
    public Guid? ClaimToken { get; set; }

    /// <summary>
    /// Short lease held during a publish attempt. A crashed dispatcher cannot
    /// strand a row permanently; another replica may claim it after expiry.
    /// </summary>
    public DateTimeOffset? ClaimedUntil { get; set; }

    /// <summary>True once the row can no longer be picked up by the dispatcher.</summary>
    public bool IsDeadLettered => DeadLetteredAt is not null;

    /// <summary>
    /// Backoff for the given (already incremented) attempt count:
    /// 2s, 4s, 8s, 16s, 32s … capped at 60s.
    /// Compared as seconds BEFORE constructing the TimeSpan: 2^n overflows
    /// TimeSpan's range long before it overflows a double, so a large attempt
    /// count must clamp rather than throw (found by unit test, attempt=100).
    /// </summary>
    public static TimeSpan BackoffFor(int retryCount)
    {
        var seconds = Math.Pow(2, Math.Max(retryCount, 1));
        return seconds >= MaxBackoff.TotalSeconds ? MaxBackoff : TimeSpan.FromSeconds(seconds);
    }
}
