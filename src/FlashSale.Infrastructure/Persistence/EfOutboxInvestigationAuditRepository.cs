using FlashSale.Application.AgentOps;
using FlashSale.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of IOutboxInvestigationAuditRepository.
/// Persists investigation results to the authoritative FlashSale store.
/// </summary>
public sealed class EfOutboxInvestigationAuditRepository(AppDbContext db)
    : IOutboxInvestigationAuditRepository
{
    public async Task RecordAsync(
        InvestigationResult result,
        string? assessedBy,
        CancellationToken ct = default)
    {
        db.OutboxInvestigationAudits.Add(new OutboxInvestigationAudit
        {
            Id = Guid.NewGuid(),
            MessageId = result.MessageId,
            Verdict = result.Verdict.ToString(),
            Recommendation = result.Recommendation,
            EvidenceHash = result.Evidence.EvidenceHash,
            AssessedBy = assessedBy,
            CreatedAt = result.CompletedAt
        });
        await db.SaveChangesAsync(ct);
    }
}
