using OpsBridge.Domain.Notifications;

namespace OpsBridge.Application.Notifications;

/// <summary>
/// Port for the notification bounded context. Provider-agnostic: callers send
/// <see cref="NotificationRequest"/> and the provider decides the channel
/// (email / SMS / push / webhook). This keeps the orchestration layer
/// (outbox, retry, dead-letter) independent of the transport.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Send a one-off notification without attaching to an outbox transaction.
    /// </summary>
    Task<NotificationResult> SendAsync(NotificationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Send a notification inside the current outbox transaction, so the recipient
    /// is guaranteed to either receive it or see it retried.
    /// </summary>
    Task<NotificationResult> SendWithOutboxAsync(NotificationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Enqueue batch notification (e.g. low-stock alerts). Returns the batch ID for
    /// later reconciliation.
    /// </summary>
    Task<string> EnqueueBatchAsync(string batchName, IEnumerable<NotificationRequest> requests, CancellationToken ct = default);

    /// <summary>
    /// Get pending notifications for a recipient (used by console/worker to drain).
    /// </summary>
    Task<IReadOnlyList<NotificationRequest>> GetPendingAsync(string recipientId, CancellationToken ct = default);
}
