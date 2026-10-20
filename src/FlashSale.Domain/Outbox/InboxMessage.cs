namespace FlashSale.Domain.Outbox;

/// <summary>
/// Deduplication Inbox pattern entity (Phase 10, ADR-019).
/// Records processed event message IDs per consumer group to guarantee idempotency.
///
/// <para>
/// ADR-019 changed the write order: the consumer CLAIMS the message id BEFORE
/// it applies side effects (unique constraint on MessageId + ConsumerName), and
/// marks it processed afterwards. A redelivery therefore cannot apply a side
/// effect twice. If a consumer dies mid-processing, the claim is stale and the
/// message is taken over after <see cref="ClaimLease"/> — at-least-once for the
/// crash, exactly-once for the duplicate.
/// </para>
/// </summary>
public class InboxMessage
{
    /// <summary>
    /// How long a claim is honoured before another worker may take the message
    /// over. Must exceed the worst-case processing time of a consumer.
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    public Guid MessageId { get; set; }
    public string ConsumerName { get; set; } = string.Empty;

    /// <summary>When the message was fully processed. NULL while claimed.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>When the current worker claimed the message. NULL once processed.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }
}
