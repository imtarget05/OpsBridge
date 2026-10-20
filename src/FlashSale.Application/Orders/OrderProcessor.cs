using FlashSale.Application.Messaging;
using FlashSale.Application.Outbox;
using FlashSale.Application.Persistence;
using FlashSale.Domain;
using FlashSale.Domain.Events;
using Microsoft.Extensions.Logging;

namespace FlashSale.Application.Orders;

/// <summary>
/// Use case: fulfill one order, idempotently (ADR-004, ADR-019).
/// Depends only on ports — delivery may be at-least-once, business effects
/// are exactly-once via the repository's unique idempotency constraint.
///
/// <para>
/// ADR-019: the order row and its <c>order.created</c> event are written in
/// ONE transaction. The event goes into the transactional outbox, not to the
/// broker — the dispatcher publishes it later. A crash between "row committed"
/// and "event published" therefore cannot lose the event, and a failure while
/// writing the outbox rolls the order back.
/// </para>
/// </summary>
public sealed class OrderProcessor(
    IOrderRepository repository,
    IWriteTransaction transaction,
    OutboxEventWriter outboxWriter,
    ILogger<OrderProcessor> logger)
{
    /// <summary>1 initial attempt + 3 retries, then the message is dead-lettered.</summary>
    public const int MaxAttempts = 4;

    public async Task ProcessAsync(OrderMessage message, CancellationToken ct)
    {
        // Idempotency: skip if this key already produced an order.
        if (await repository.ExistsAsync(message.IdempotencyKey, ct))
        {
            logger.LogInformation(
                "Duplicate delivery for {Key} — already persisted, acknowledging.",
                message.IdempotencyKey);
            return;
        }

        await transaction.BeginAsync(ct);
        try
        {
            if (!await repository.PersistAsync(message, ct))
            {
                throw new StockDriftException(
                    $"DB stock insufficient for product {message.ProductId} " +
                    $"(idempotency key {message.IdempotencyKey}). Possible Redis/DB drift; " +
                    "run POST /internal/resync-stock/{id} after resolving.");
            }

            logger.LogInformation(
                "Order persisted {Key} (product {Pid}, qty {Qty}, attempt {Attempt}).",
                message.IdempotencyKey, message.ProductId, message.Quantity, message.Attempt + 1);

            // Spec §4: the row exists now → order.created carries the real order id.
            // ADR-019: written in the SAME transaction as the order row.
            var orderId = await repository.GetOrderIdAsync(message.IdempotencyKey, ct) ?? 0;
            await outboxWriter.EnqueueAsync(
                new OrderCreatedEvent(
                    Guid.NewGuid().ToString("D"),
                    message.CorrelationId ?? Guid.NewGuid(),
                    "order-worker",
                    DateTimeOffset.UtcNow,
                    orderId,
                    message.ProductId,
                    message.Quantity,
                    message.UserId,
                    message.IdempotencyKey,
                    message.PaymentDueAt is null ? OrderStatus.Completed : OrderStatus.PendingPayment),
                OutboxEventWriter.OrderAggregate,
                orderId.ToString(),
                ct);

            await transaction.CommitAsync(ct);
        }
        catch
        {
            // The order row and the event row leave together: no committed
            // order without its event, no event without its order.
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
