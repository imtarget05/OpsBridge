using FlashSale.Application.Persistence;

namespace FlashSale.Application.Automation;

public sealed record StuckOrderScanResult(int Scanned, int AlertsCreated);

/// <summary>
/// Detects accepted orders whose queue-to-persistence step exceeded the SLA.
/// PaymentPending and Confirmed are intentionally outside this rule: they have
/// separate lifecycle semantics and no fulfillment-complete transition today.
/// </summary>
public sealed class StuckOrderScanUseCase(
    IStuckOrderRepository repository,
    StuckOrderOptions options,
    TimeProvider clock)
{
    public const int BatchSize = 200;

    private DateTimeOffset Cutoff() =>
        clock.GetUtcNow().AddMinutes(-Math.Max(1, options.ThresholdMinutes));

    public Task<IReadOnlyList<StuckOrderCandidate>> GetCurrentAsync(CancellationToken ct = default) =>
        repository.FindUnfulfilledAsync(Cutoff(), BatchSize, ct);

    public async Task<StuckOrderScanResult> ExecuteAsync(CancellationToken ct = default)
    {
        var detectedAt = clock.GetUtcNow();
        var thresholdMinutes = Math.Max(1, options.ThresholdMinutes);
        var candidates = await repository.FindUnfulfilledAsync(
            detectedAt.AddMinutes(-thresholdMinutes), BatchSize, ct);
        var raised = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await repository.TryRaiseAlertAsync(candidate, detectedAt, thresholdMinutes, ct))
                raised++;
        }

        return new StuckOrderScanResult(candidates.Count, raised);
    }
}
