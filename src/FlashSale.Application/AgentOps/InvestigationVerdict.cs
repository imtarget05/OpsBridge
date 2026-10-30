namespace FlashSale.Application.AgentOps;

/// <summary>
/// Triage classification for a single outbox event investigation (§3.1).
/// Only DEAD_LETTERED can receive a recovery recommendation.
/// </summary>
public enum InvestigationVerdict
{
    /// <summary>Row is dead-lettered (terminal, eligible for recovery recommendation).</summary>
    DeadLetterRecoveryRecommended,

    /// <summary>Row is waiting out a backoff deadline; not yet dead-lettered.</summary>
    BackingOffNoAction,

    /// <summary>Row has already been processed; no action needed.</summary>
    AlreadyProcessedNoAction,

    /// <summary>No row exists for the given MessageId.</summary>
    NotFoundNoAction,

    /// <summary>Row was dead-lettered but is older than the staleness window.</summary>
    StaleRequiresNewRun,

    /// <summary>Row exists but lacks critical metadata; agent abstains.</summary>
    InsufficientEvidenceAbstain
}
