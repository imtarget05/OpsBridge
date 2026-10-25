using System.Security.Cryptography;
using System.Text;
using FlashSale.Application.Outbox;
using FlashSale.Application.Persistence;
using FlashSale.Domain.Events;
using FlashSale.Domain.Outbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// Reads only FlashSale-owned data. The committed inventory.reserved outbox row
/// is the durable acceptance marker; an Order row is the fulfillment result.
/// </summary>
public sealed class StuckOrderRepository(AppDbContext db, OutboxEventWriter events) : IStuckOrderRepository
{
    public async Task<IReadOnlyList<StuckOrderCandidate>> FindUnfulfilledAsync(
        DateTimeOffset acceptedBefore,
        int take,
        CancellationToken ct = default)
    {
        var rows = await db.OutboxMessages.AsNoTracking()
            .Where(r => r.EventType == "inventory.reserved"
                && r.AggregateType == OutboxEventWriter.OrderAggregate
                && r.AggregateId != null
                && r.CorrelationId != null
                && r.CreatedAt <= acceptedBefore
                && !db.Orders.Any(o => o.IdempotencyKey == r.AggregateId)
                && !db.OutboxMessages.Any(other =>
                    other.AggregateType == OutboxEventWriter.OrderAggregate
                    && other.AggregateId == r.AggregateId
                    && other.CorrelationId == r.CorrelationId
                    && (other.EventType == "inventory.released"
                        || other.EventType == "inventory.compensation_failed")))
            .OrderBy(r => r.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .Select(r => new { r.MessageId, r.CorrelationId, r.AggregateId, r.CreatedAt })
            .ToListAsync(ct);

        return rows.Select(r => new StuckOrderCandidate(
            r.MessageId, r.CorrelationId!.Value, r.AggregateId!, r.CreatedAt)).ToArray();
    }

    public async Task<bool> TryRaiseAlertAsync(
        StuckOrderCandidate candidate,
        DateTimeOffset detectedAt,
        int thresholdMinutes,
        CancellationToken ct = default)
    {
        var alertId = AlertIdFor(candidate.ReservationEventId);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // State may have advanced between FindUnfulfilledAsync and here.
            var stillUnfulfilled = await db.OutboxMessages.AsNoTracking().AnyAsync(r =>
                r.MessageId == candidate.ReservationEventId
                && r.EventType == "inventory.reserved"
                && r.AggregateType == OutboxEventWriter.OrderAggregate
                && r.AggregateId == candidate.IdempotencyKey
                && r.CorrelationId == candidate.CorrelationId
                && !db.Orders.Any(o => o.IdempotencyKey == r.AggregateId)
                && !db.OutboxMessages.Any(other =>
                    other.AggregateType == OutboxEventWriter.OrderAggregate
                    && other.AggregateId == r.AggregateId
                    && other.CorrelationId == r.CorrelationId
                    && (other.EventType == "inventory.released"
                        || other.EventType == "inventory.compensation_failed")), ct);

            if (!stillUnfulfilled || await db.OutboxMessages.AsNoTracking()
                    .AnyAsync(row => row.MessageId == alertId, ct))
            {
                await transaction.RollbackAsync(ct);
                return false;
            }

            await events.EnqueueAsync(new OrderFulfillmentStuckEvent(
                    alertId.ToString("D"), candidate.CorrelationId,
                    "automation/stuck-order", detectedAt,
                    candidate.IdempotencyKey, candidate.ReservationEventId,
                    candidate.AcceptedAt, thresholdMinutes),
                OutboxEventWriter.OrderAggregate, candidate.IdempotencyKey, ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation,
              ConstraintName: "IX_OutboxMessages_MessageId" })
        {
            await transaction.RollbackAsync(ct);
            foreach (var entry in db.ChangeTracker.Entries<OutboxMessage>()
                         .Where(entry => entry.State == EntityState.Added))
                entry.State = EntityState.Detached;
            return false;
        }
        catch (DuplicateOutboxMessageException)
        {
            // OutboxRepository translates the MessageId unique-index race into
            // this typed condition after detaching its failed Added row.
            await transaction.RollbackAsync(ct);
            return false;
        }
    }

    private static Guid AlertIdFor(Guid reservationEventId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"order.fulfillment_stuck:{reservationEventId:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
