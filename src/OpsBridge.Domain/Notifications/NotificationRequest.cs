namespace OpsBridge.Domain.Notifications;

/// <summary>
/// A single notification to be delivered to one or more recipients.
/// </summary>
public sealed class NotificationRequest
{
    public string? Id { get; set; }

    /// <summary>Recipient identifier (email, phone, user id, connector id).</summary>
    public string RecipientId { get; set; } = string.Empty;

    /// <summary>Human-readable recipient (e.g. "buabinh@opsbridge.internal").</summary>
    public string? RecipientLabel { get; set; }

    /// <summary>Channel: email, sms, push, webhook.</summary>
    public string Channel { get; set; } = "email";

    /// <summary>Subject (for email/sms).</summary>
    public string? Subject { get; set; }

    /// <summary>Plain-text body. HTML optional — do not embed unsanitized markup.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Structured context (order id, event name, etc.).</summary>
    public Dictionary<string, string> Context { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static NotificationRequest Create(string recipientId, string channel, string body, string? subject = null)
    {
        return new NotificationRequest
        {
            RecipientId = recipientId,
            Channel = channel,
            Body = body,
            Subject = subject,
        };
    }
}
