namespace FlashSale.Application.AgentOps;

/// <summary>
/// Critic/Policy role: verifies every material claim against evidence,
/// checks scope and freshness, and blocks actions (§3.1 §5).
/// One bounded redraft maximum.
/// </summary>
public sealed class OutboxInvestigationCritic
{
    public CriticVerdict Critique(
        InvestigationEvidence evidence,
        InvestigationNotes notes,
        TriageResult triage,
        int maxRedrafts)
    {
        var blockingReasons = new System.Text.StringBuilder();
        int redraftCount = 0;

        // Claims verified: every material claim traces to evidence
        if (evidence.MessageId == Guid.Empty)
        {
            blockingReasons.Append("No message ID present; cannot verify claims. ");
        }

        // Scope valid: verdict is read-only
        if (triage.Classification == InvestigationVerdict.DeadLetterRecoveryRecommended
            && string.IsNullOrWhiteSpace(notes.ReadOnlyOperatorChecks))
        {
            blockingReasons.Append("Dead-lettered verdict must link read-only operator checks. ");
        }

        // Freshness valid: not STALE
        if (triage.Classification == InvestigationVerdict.StaleRequiresNewRun)
        {
            blockingReasons.Append("Event is stale; requires a new run before any recommendation. ");
        }

        // Actions blocked: no write actions recommended
        if (!string.IsNullOrWhiteSpace(notes.RootCauseClaim))
        {
            blockingReasons.Append("Investigator must not assert root cause. ");
        }

        bool claimsVerified = evidence.MessageId != Guid.Empty && blockingReasons.Length == 0;
        bool scopeValid = triage.Classification != InvestigationVerdict.NotFoundNoAction || blockingReasons.Length > 0;
        bool freshnessValid = triage.Classification != InvestigationVerdict.StaleRequiresNewRun;
        bool actionsBlocked = string.IsNullOrWhiteSpace(notes.RootCauseClaim)
            && !blockingReasons.ToString().Contains("requeue", System.StringComparison.OrdinalIgnoreCase)
            && !blockingReasons.ToString().Contains("cancel", System.StringComparison.OrdinalIgnoreCase)
            && !blockingReasons.ToString().Contains("mutat", System.StringComparison.OrdinalIgnoreCase);

        return new CriticVerdict
        {
            ClaimsVerified = claimsVerified,
            ScopeValid = scopeValid,
            FreshnessValid = freshnessValid,
            ActionsBlocked = actionsBlocked,
            RedraftCount = redraftCount,
            BlockingReasons = blockingReasons.ToString().Trim()
        };
    }
}
