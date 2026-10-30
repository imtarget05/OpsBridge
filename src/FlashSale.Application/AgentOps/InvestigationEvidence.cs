using FlashSale.Domain.Outbox;

namespace FlashSale.Application.AgentOps;

/// <summary>
/// Read-only evidence captured from the outbox store for one investigation.
/// No payloads or secrets are captured — only metadata (§3.1 §2).
/// </summary>
public sealed class InvestigationEvidence
{
    public Guid MessageId { get; init; }
    public string? EventType { get; init; }
    public string? Topic { get; init; }
    public string? AggregateType { get; init; }
    public string? AggregateId { get; init; }
    public Guid? CorrelationId { get; init; }
    public int SchemaVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public int RetryCount { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset? DeadLetteredAt { get; init; }
    public bool IsDeadLettered => DeadLetteredAt is not null;
    public DateTimeOffset ObservationTimestamp { get; init; } = DateTimeOffset.UtcNow;
    public string SourceSha { get; init; } = string.Empty;
    public string EvidenceHash { get; init; } = string.Empty;

    public static InvestigationEvidence FromOutboxMessage(
        OutboxMessage msg,
        string sourceSha,
        string evidenceHash)
    {
        return new InvestigationEvidence
        {
            MessageId = msg.MessageId,
            EventType = msg.EventType,
            Topic = msg.Topic,
            AggregateType = msg.AggregateType,
            AggregateId = msg.AggregateId,
            CorrelationId = msg.CorrelationId,
            SchemaVersion = msg.SchemaVersion,
            CreatedAt = msg.CreatedAt,
            ProcessedAt = msg.ProcessedAt,
            RetryCount = msg.RetryCount,
            Error = msg.Error,
            NextAttemptAt = msg.NextAttemptAt,
            DeadLetteredAt = msg.DeadLetteredAt,
            ObservationTimestamp = DateTimeOffset.UtcNow,
            SourceSha = sourceSha,
            EvidenceHash = evidenceHash,
        };
    }
}
