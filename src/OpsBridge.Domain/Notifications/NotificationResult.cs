using System.Text.Json;

namespace OpsBridge.Domain.Notifications;

/// <summary>
/// Result of a notification attempt. Includes idempotency, retry, and
/// dead-letter metadata for reconciliation and audit.
/// </summary>
public sealed class NotificationResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string RequestId { get; set; } = string.Empty;

    public string RecipientId { get; set; } = string.Empty;

    public string Channel { get; set; } = string.Empty;

    public bool Sent { get; set; }

    public string? Status { get; set; }

    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; }

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;

    public string? OutboxId { get; set; }

    public bool IsTransient { get; set; }

    public static NotificationResult Success(string requestId, string recipientId, string channel, string? outboxId = null)
    {
        return new NotificationResult
        {
            RequestId = requestId,
            RecipientId = recipientId,
            Channel = channel,
            Sent = true,
            Status = "Sent",
            OutboxId = outboxId,
        };
    }

    public static NotificationResult Failure(string requestId, string recipientId, string channel, string error, bool isTransient, string? outboxId = null)
    {
        return new NotificationResult
        {
            RequestId = requestId,
            RecipientId = recipientId,
            Channel = channel,
            Sent = false,
            Status = "Failed",
            ErrorMessage = error,
            IsTransient = isTransient,
            OutboxId = outboxId,
        };
    }

    public static implicit operator JsonElement(NotificationResult? _) => throw new NotSupportedException("Use ToJson() or a DTO mapping.");
}
