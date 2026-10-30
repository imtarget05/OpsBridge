namespace FlashSale.Application.AgentOps;

/// <summary>
/// Final result of the bounded investigation workflow: Triage → Investigator → Critic.
/// Read-only recommendation only — no requeue, payment cancel, order mutation,
/// or inventory mutation (§3.1 §7).
/// </summary>
public sealed class InvestigationResult
{
    public Guid MessageId { get; init; }
    public InvestigationVerdict Verdict { get; init; }
    public InvestigationEvidence Evidence { get; init; } = new();
    public TriageResult Triage { get; init; } = new();
    public InvestigationNotes Investigation { get; init; } = new();
    public CriticVerdict Critic { get; init; } = new();
    public string Recommendation { get; init; } = string.Empty;
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsReadOnly { get; init; } = true;
}
