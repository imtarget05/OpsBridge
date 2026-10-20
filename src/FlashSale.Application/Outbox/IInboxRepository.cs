namespace FlashSale.Application.Outbox;

/// <summary>
/// Port for Deduplication Inbox repository (Phase 10, ADR-019).
/// </summary>
public interface IInboxRepository
{
    Task<bool> HasBeenProcessedAsync(Guid messageId, string consumerName, CancellationToken ct = default);

    Task MarkProcessedAsync(Guid messageId, string consumerName, CancellationToken ct = default);

    /// <summary>
    /// Claim a message id for processing (ADR-019).
    ///
    /// <para>
    /// Returns <c>true</c> when THIS caller owns the message and may apply
    /// side effects; <c>false</c> when the message was already processed (a
    /// duplicate delivery) or is currently claimed by another worker.
    /// </para>
    ///
    /// <para>
    /// The claim is durable: it is a row in the inbox table protected by the
    /// unique constraint on (MessageId, ConsumerName), so two concurrent
    /// deliveries of the same event cannot both win.
    /// </para>
    /// </summary>
    Task<bool> TryClaimAsync(Guid messageId, string consumerName, CancellationToken ct = default);
}
