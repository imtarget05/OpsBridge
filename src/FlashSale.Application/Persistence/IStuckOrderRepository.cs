namespace FlashSale.Application.Persistence;

/// <summary>
/// A reservation committed before queue handoff whose authoritative order row
/// has not appeared. A released/compensated reservation is never a candidate.
/// </summary>
public sealed record StuckOrderCandidate(
    Guid ReservationEventId,
    Guid CorrelationId,
    string IdempotencyKey,
    DateTimeOffset AcceptedAt);

/// <summary>FlashSale-owned read/write port for accepted-order stall alerts.</summary>
public interface IStuckOrderRepository
{
    Task<IReadOnlyList<StuckOrderCandidate>> FindUnfulfilledAsync(
        DateTimeOffset acceptedBefore,
        int take,
        CancellationToken ct = default);

    /// <summary>
    /// Rechecks current state and records one durable alert event per accepted
    /// reservation. Returns false if fulfilled, compensated, or already alerted.
    /// </summary>
    Task<bool> TryRaiseAlertAsync(
        StuckOrderCandidate candidate,
        DateTimeOffset detectedAt,
        int thresholdMinutes,
        CancellationToken ct = default);
}
