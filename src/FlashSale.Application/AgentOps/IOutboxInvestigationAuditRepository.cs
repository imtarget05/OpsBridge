using System.Threading;
using System.Threading.Tasks;

namespace FlashSale.Application.AgentOps;

/// <summary>
/// Port for recording completed outbox investigations to an audit trail.
/// </summary>
public interface IOutboxInvestigationAuditRepository
{
    Task RecordAsync(InvestigationResult result, string? assessedBy, CancellationToken ct = default);
}
