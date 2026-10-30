namespace FlashSale.Application.AgentOps;

/// <summary>
/// Triage role: classifies an outbox event into one of six verdicts.
/// Deterministic — no model call involved (§3.1 §3).
/// </summary>
public sealed class OutboxInvestigationTriage
{
    /// <summary>
    /// Classifies the evidence into a triage verdict.
    /// </summary>
    /// <param name="evidence">The outbox evidence to classify.</param>
    /// <param name="stalenessMinutes">Age beyond which a dead-lettered event is STALE.</param>
    /// <param name="now">Current UTC time for age calculation (injected for testability).</param>
    public TriageResult Classify(InvestigationEvidence evidence, int stalenessMinutes, DateTimeOffset now)
    {
        // NOT_FOUND: no row exists
        if (evidence.MessageId == Guid.Empty)
        {
            return new TriageResult
            {
                Classification = InvestigationVerdict.NotFoundNoAction,
                Reasoning = "No outbox row exists for the requested MessageId."
            };
        }

        // ALREADY_PROCESSED: row has been processed
        if (evidence.ProcessedAt is not null)
        {
            return new TriageResult
            {
                Classification = InvestigationVerdict.AlreadyProcessedNoAction,
                Reasoning = $"Event was already processed at {evidence.ProcessedAt.Value:O}. No action needed."
            };
        }

        // DEAD_LETTERED vs STALE
        if (evidence.IsDeadLettered)
        {
            var ageMinutes = (now - evidence.DeadLetteredAt!.Value).TotalMinutes;
            if (ageMinutes > stalenessMinutes)
            {
                return new TriageResult
                {
                    Classification = InvestigationVerdict.StaleRequiresNewRun,
                    Reasoning = $"Event was dead-lettered {ageMinutes:F0} minutes ago, exceeding the {stalenessMinutes}-minute staleness window."
                };
            }
            return new TriageResult
            {
                Classification = InvestigationVerdict.DeadLetterRecoveryRecommended,
                Reasoning = $"Event is dead-lettered (DeadLetteredAt={evidence.DeadLetteredAt.Value:O}) and within the staleness window. Eligible for recovery recommendation."
            };
        }

        // BACKING_OFF: not processed, not dead-lettered, waiting on backoff
        if (evidence.NextAttemptAt is not null && evidence.NextAttemptAt.Value > now)
        {
            return new TriageResult
            {
                Classification = InvestigationVerdict.BackingOffNoAction,
                Reasoning = $"Event is waiting out a backoff until {evidence.NextAttemptAt.Value:O}. Not yet dead-lettered."
            };
        }

        // INSUFFICIENT_EVIDENCE: missing critical metadata
        if (string.IsNullOrWhiteSpace(evidence.EventType) || string.IsNullOrWhiteSpace(evidence.Topic))
        {
            return new TriageResult
            {
                Classification = InvestigationVerdict.InsufficientEvidenceAbstain,
                Reasoning = "Event row exists but lacks EventType or Topic metadata; agent abstains."
            };
        }

        // Fallback: not dead-lettered, not backing off, not processed — treat as backing off
        return new TriageResult
        {
            Classification = InvestigationVerdict.BackingOffNoAction,
            Reasoning = "Event is not processed, not dead-lettered, and has no pending backoff deadline."
        };
    }
}
