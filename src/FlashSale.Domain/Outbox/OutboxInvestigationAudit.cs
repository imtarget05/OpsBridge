namespace FlashSale.Domain.Outbox;

/// <summary>
/// Immutable audit record of one agent investigation into a dead-lettered
/// outbox event. Read-only evidence trail (§3.1 §6).
/// </summary>
public sealed class OutboxInvestigationAudit
{
    public Guid Id { get; init; }
    public Guid MessageId { get; init; }
    public string Verdict { get; init; } = string.Empty;
    public string? Recommendation { get; init; }
    public string? EvidenceHash { get; init; }
    public string? AssessedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
