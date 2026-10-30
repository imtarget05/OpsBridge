using System.Security.Cryptography;
using System.Text;
using FlashSale.Application.Outbox;
using FlashSale.Domain.Outbox;

namespace FlashSale.Application.AgentOps;

/// <summary>
/// Read-only investigation workflow for dead-lettered outbox events.
/// Orchestrates Triage → Investigator → Critic without mutating outbox state:
/// no requeue, no mark-processed, no mark-failed, and no enqueue (§3.1 §7).
/// </summary>
public sealed class OutboxInvestigationWorkflow
{
    private readonly IOutboxRepository _outbox;
    private readonly IOutboxInvestigationAuditRepository _audit;
    private readonly AgentOpsOptions _options;

    public OutboxInvestigationWorkflow(
        IOutboxRepository outbox,
        IOutboxInvestigationAuditRepository audit,
        AgentOpsOptions options)
    {
        _outbox = outbox;
        _audit = audit;
        _options = options;
    }

    /// <summary>
    /// Investigates the outbox message identified by <paramref name="messageId"/>.
    /// The workflow is read-only: it reads the outbox row, runs the triage,
    /// investigator and critic agents, records the result to the audit trail,
    /// and returns it. No outbox state is mutated.
    /// </summary>
    public async Task<InvestigationResult> InvestigateAsync(
        Guid messageId,
        string? assessedBy,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return new InvestigationResult
            {
                MessageId = messageId,
                Verdict = InvestigationVerdict.NotFoundNoAction,
                Recommendation = "AgentOps is disabled; no investigation performed.",
                IsReadOnly = true
            };
        }

        var message = await _outbox.GetByMessageIdAsync(messageId, ct);
        var now = DateTimeOffset.UtcNow;

        var evidence = message is null
            ? new InvestigationEvidence
            {
                MessageId = messageId,
                SourceSha = _options.SourceSha,
                EvidenceHash = string.Empty
            }
            : InvestigationEvidence.FromOutboxMessage(message, _options.SourceSha, ComputeEvidenceHash(message));

        var triage = new OutboxInvestigationTriage().Classify(evidence, _options.StalenessMinutes, now);
        var notes = new OutboxInvestigationInvestigator().Investigate(evidence, triage);
        var critic = new OutboxInvestigationCritic().Critique(evidence, notes, triage, _options.MaxRedrafts);

        var recommendation = BuildRecommendation(triage, messageId, critic);

        var result = new InvestigationResult
        {
            MessageId = messageId,
            Verdict = triage.Classification,
            Evidence = evidence,
            Triage = triage,
            Investigation = notes,
            Critic = critic,
            Recommendation = recommendation,
            CompletedAt = now,
            IsReadOnly = true
        };

        await _audit.RecordAsync(result, assessedBy, ct);
        return result;
    }

    private static string BuildRecommendation(TriageResult triage, Guid messageId, CriticVerdict critic)
    {
        if (triage.Classification == InvestigationVerdict.DeadLetterRecoveryRecommended)
        {
            return $"Recommend operator-approved requeue of message {messageId} via POST /internal/agent/outbox/{messageId}/requeue after human review. Agent cannot execute requeue.";
        }
        return $"No recovery action recommended. Verdict: {triage.Classification}.";
    }

    private static string ComputeEvidenceHash(OutboxMessage msg)
    {
        var input = $"{msg.MessageId}|{msg.EventType}|{msg.Topic}|{msg.RetryCount}|{msg.DeadLetteredAt}|{msg.ProcessedAt}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
