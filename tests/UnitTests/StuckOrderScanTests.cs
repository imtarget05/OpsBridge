using FlashSale.Application.Automation;
using FlashSale.Application.Persistence;
using FlashSale.Domain.Entities;
using FlashSale.Domain.Outbox;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.UnitTests;

public sealed class StuckOrderScanTests
{
    [Fact]
    public async Task Scan_UsesInjectedClock_AndAlertsOnlyAfterDeadline_Once()
    {
        var acceptedAt = new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero);
        var candidate = new StuckOrderCandidate(Guid.NewGuid(), Guid.NewGuid(), "accepted-1", acceptedAt);
        var repository = new FakeRepository(candidate);
        var clock = new MutableClock(acceptedAt.AddMinutes(4).AddSeconds(59));
        var scan = new StuckOrderScanUseCase(repository,
            new StuckOrderOptions { ThresholdMinutes = 5 }, clock);

        Assert.Equal(new StuckOrderScanResult(0, 0), await scan.ExecuteAsync());
        clock.Now = acceptedAt.AddMinutes(5);
        Assert.Equal(new StuckOrderScanResult(1, 1), await scan.ExecuteAsync());
        Assert.Equal(new StuckOrderScanResult(1, 0), await scan.ExecuteAsync());
        Assert.Single(repository.Alerted);
        Assert.Equal(5, repository.ThresholdMinutes);
    }

    [Fact]
    public async Task Scan_DoesNotAlert_WhenOrderRecoversBeforeRecording()
    {
        var acceptedAt = new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero);
        var repository = new FakeRepository(new StuckOrderCandidate(
            Guid.NewGuid(), Guid.NewGuid(), "accepted-2", acceptedAt))
        {
            ResolveOnAlert = true
        };
        var scan = new StuckOrderScanUseCase(repository,
            new StuckOrderOptions { ThresholdMinutes = 5 },
            new MutableClock(acceptedAt.AddMinutes(6)));

        Assert.Equal(new StuckOrderScanResult(1, 0), await scan.ExecuteAsync());
        Assert.Empty(await scan.GetCurrentAsync());
    }

    [Fact]
    public async Task ConcurrentScans_RecordOneAlertForTheSameReservation()
    {
        var acceptedAt = new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero);
        var repository = new FakeRepository(new StuckOrderCandidate(
            Guid.NewGuid(), Guid.NewGuid(), "accepted-concurrent", acceptedAt))
        {
            WaitForTwoScans = true
        };
        var scan = new StuckOrderScanUseCase(repository,
            new StuckOrderOptions { ThresholdMinutes = 5 },
            new MutableClock(acceptedAt.AddMinutes(6)));

        var results = await Task.WhenAll(scan.ExecuteAsync(), scan.ExecuteAsync());

        Assert.Equal(1, results.Sum(result => result.AlertsCreated));
        Assert.Single(repository.Alerted);
    }

    [Fact]
    public async Task Query_ExcludesPersistedCompensatedAndPublicationOnlyStalls()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var time = new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero);
        var open = AddReservation(db, "open", time);
        AddReservation(db, "persisted", time);
        var released = AddReservation(db, "released", time);
        var compensationFailed = AddReservation(db, "compensation-failed", time);
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = "order.created", AggregateType = "order", AggregateId = "publication-stuck",
            CorrelationId = Guid.NewGuid(), CreatedAt = time,
            DeadLetteredAt = time.AddMinutes(1)
        });
        db.Orders.Add(new Order { IdempotencyKey = "persisted", CreatedAt = time.UtcDateTime });
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = "inventory.released", AggregateType = "order", AggregateId = "released",
            CorrelationId = released.CorrelationId, CreatedAt = time.AddMinutes(1)
        });
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventType = "inventory.compensation_failed", AggregateType = "order",
            AggregateId = "compensation-failed", CorrelationId = compensationFailed.CorrelationId,
            CreatedAt = time.AddMinutes(1)
        });
        await db.SaveChangesAsync();

        var repo = new StuckOrderRepository(db, null!); // Read-only query does not use writer.
        var found = await repo.FindUnfulfilledAsync(time.AddMinutes(5), 200);

        Assert.Single(found);
        Assert.Equal(open.MessageId, found[0].ReservationEventId);
        Assert.Equal("open", found[0].IdempotencyKey);
    }

    private static OutboxMessage AddReservation(AppDbContext db, string key, DateTimeOffset at)
    {
        var row = new OutboxMessage
        {
            EventType = "inventory.reserved", AggregateType = "order", AggregateId = key,
            CorrelationId = Guid.NewGuid(), CreatedAt = at
        };
        db.OutboxMessages.Add(row);
        return row;
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeRepository(StuckOrderCandidate candidate) : IStuckOrderRepository
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _twoScansStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _scanCalls;
        public HashSet<Guid> Alerted { get; } = [];
        public bool ResolveOnAlert { get; set; }
        public bool WaitForTwoScans { get; set; }
        public bool Resolved { get; private set; }
        public int ThresholdMinutes { get; private set; }

        public Task<IReadOnlyList<StuckOrderCandidate>> FindUnfulfilledAsync(
            DateTimeOffset acceptedBefore, int take, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StuckOrderCandidate>>(
                !Resolved && candidate.AcceptedAt <= acceptedBefore ? [candidate] : []);

        public async Task<bool> TryRaiseAlertAsync(
            StuckOrderCandidate value, DateTimeOffset detectedAt,
            int thresholdMinutes, CancellationToken ct = default)
        {
            ThresholdMinutes = thresholdMinutes;
            if (WaitForTwoScans)
            {
                if (Interlocked.Increment(ref _scanCalls) == 2)
                    _twoScansStarted.SetResult();
                await _twoScansStarted.Task.WaitAsync(ct);
            }
            if (ResolveOnAlert) Resolved = true;
            lock (_gate)
                return !Resolved && Alerted.Add(value.ReservationEventId);
        }
    }
}
