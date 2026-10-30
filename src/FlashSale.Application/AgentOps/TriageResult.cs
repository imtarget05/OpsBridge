namespace FlashSale.Application.AgentOps;

/// <summary>
/// Output of the Triage role: classifies the event state and reasoning.
/// Deterministic — no model call involved.
/// </summary>
public sealed class TriageResult
{
    public InvestigationVerdict Classification { get; init; }
    public string Reasoning { get; init; } = string.Empty;

    /// <summary>Only DEAD_LETTERED can receive a recovery recommendation.</summary>
    public bool CanReceiveRecoveryRecommendation => Classification == InvestigationVerdict.DeadLetterRecoveryRecommended;
}
