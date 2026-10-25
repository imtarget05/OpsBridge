using OpsBridge.Domain.StuckOrder;

namespace OpsBridge.Application.StuckOrder;

public interface IStuckOrderAlertRepository
{
    Task AddAsync(StuckOrderAlert alert, CancellationToken ct = default);
    Task<StuckOrderAlert?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<StuckOrderAlert?> GetByPurchaseOrderIdAsync(Guid purchaseOrderId, CancellationToken ct = default);
    Task<IReadOnlyList<StuckOrderAlert>> GetOpenAlertsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StuckOrderAlert>> GetAlertsByStatusAsync(StuckOrderStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<StuckOrderAlert>> GetDeadLetteredAlertsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<StuckOrderAlert>> GetAlertsForRetryAsync(TimeSpan retryInterval, int maxRetries, CancellationToken ct = default);
    Task UpdateAsync(StuckOrderAlert alert, CancellationToken ct = default);
    Task<bool> ExistsForPurchaseOrderAsync(Guid purchaseOrderId, CancellationToken ct = default);
}