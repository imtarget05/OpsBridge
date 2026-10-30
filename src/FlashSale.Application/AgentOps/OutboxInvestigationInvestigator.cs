namespace FlashSale.Application.AgentOps;

/// <summary>
/// Investigator role: describes what metadata establishes, identifies unknowns,
/// and links read-only operator checks. Never claims broker/root cause (§3.1 §4).
/// </summary>
public sealed class OutboxInvestigationInvestigator
{
    public InvestigationNotes Investigate(InvestigationEvidence evidence, TriageResult triage)
    {
        var establishes = new System.Text.StringBuilder();
        establishes.Append($"Message {evidence.MessageId} of type '{evidence.EventType}' on topic '{evidence.Topic}'");
        establishes.Append($" was created at {evidence.CreatedAt:O}");
        if (evidence.AggregateType is not null && evidence.AggregateId is not null)
            establishes.Append($" for aggregate {evidence.AggregateType}/{evidence.AggregateId}");
        if (evidence.RetryCount > 0)
            establishes.Append($", with {evidence.RetryCount} retry attempt(s)");
        if (evidence.Error is not null)
            establishes.Append($", last error: {evidence.Error}");
        if (evidence.IsDeadLettered)
            establishes.Append($", dead-lettered at {evidence.DeadLetteredAt:O}");
        else if (evidence.NextAttemptAt is not null)
            establishes.Append($", next attempt at {evidence.NextAttemptAt:O}");
        establishes.Append(".");

        var unknowns = new System.Text.StringBuilder();
        unknowns.Append("Metadata alone cannot establish: ");
        unknowns.Append("the broker-level root cause of the failure, ");
        unknowns.Append("whether the consumer is still down, ");
        unknowns.Append("or whether the downstream service has recovered. ");
        unknowns.Append("The event payload is intentionally not fetched by the agent (§3.1 §2).");

        var checks = new System.Text.StringBuilder();
        checks.Append("Read-only operator checks: ");
        checks.Append("GET /api/outbox/stuck to list stuck events; ");
        checks.Append("GET /api/outbox/pending to list pending events; ");
        checks.Append($"GET /internal/agent/outbox/{evidence.MessageId} to re-read this specific event. ");
        checks.Append("No write-capable endpoint is invoked by this workflow.");

        return new InvestigationNotes
        {
            MetadataEstablishes = establishes.ToString(),
            Unknowns = unknowns.ToString(),
            ReadOnlyOperatorChecks = checks.ToString(),
            RootCauseClaim = string.Empty
        };
    }
}
