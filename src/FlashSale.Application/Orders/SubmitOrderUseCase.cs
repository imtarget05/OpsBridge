using FlashSale.Application.Automation;
using FlashSale.Application.Inventory;
using FlashSale.Application.Messaging;
using FlashSale.Application.Outbox;
using FlashSale.Application.Persistence;
using FlashSale.Domain;
using FlashSale.Domain.Events;
using Microsoft.Extensions.Logging;

namespace FlashSale.Application.Orders;

/// <summary>Outcome of a submit-order attempt.</summary>
public enum SubmitOrderOutcome
{
    /// <summary>Order accepted and enqueued for asynchronous fulfillment.</summary>
    Accepted,
    /// <summary>No stock left — fast-rejected via the reservation tier or the DB.</summary>
    OutOfStock,
    /// <summary>This idempotency key was already used.</summary>
    DuplicateRequest,
    /// <summary>The requested product does not exist.</summary>
    ProductNotFound,
    /// <summary>Queue is full — caller should retry after a short delay.</summary>
    SystemBusy,
    /// <summary>Order placed synchronously (reservation tier unavailable).</summary>
    CompletedSynchronously,
    /// <summary>Quantity must be a positive integer.</summary>
    InvalidQuantity,
    /// <summary>
    /// Reservation tier and PostgreSQL disagreed (ADR-002 guard refused the
    /// conditional UPDATE instead of overselling) — alerted, not retried.
    /// </summary>
    StockDrift
}

/// <summary>
/// Result of <see cref="SubmitOrderUseCase.ExecuteAsync"/>. The presentation
/// layer maps each <see cref="Outcome"/> to the appropriate HTTP status code.
/// </summary>
public sealed record SubmitOrderResult(
    SubmitOrderOutcome Outcome,
    string IdempotencyKey);

/// <summary>
/// Use case: accept a flash-sale purchase order (ADR-003, ADR-005, ADR-019).
///
/// Orchestrates a multi-tier reservation and fulfillment flow:
///   T1  — fast reservation via the caching tier (Redis Lua CAS).
///   T1b — unknown product: seed the cache from the DB, retry once.
///   T1c — reservation tier unavailable: fall back to synchronous DB processing.
///   T2  — record the reservation fact in the transactional outbox (ADR-019).
///   T2b — hand off to the async fulfillment queue.
///   T2c — backpressure: release the reservation and compensate the event.
///
/// Depends only on Application ports — no Infrastructure or Presentation references.
/// </summary>
public sealed class SubmitOrderUseCase(
    IStockReservationGateway reservationGateway,
    IOrderQueueProducer queueProducer,
    IOrderReadModel readModel,
    OrderProcessor processor,
    IWriteTransaction transaction,
    OutboxEventWriter outboxWriter,
    PaymentAutomationOptions paymentOptions,
    ILogger<SubmitOrderUseCase> logger)
{
    public async Task<SubmitOrderResult> ExecuteAsync(
        int productId,
        int quantity,
        string idempotencyKey,
        Guid? userId = null,
        CancellationToken ct = default)
    {
        if (quantity <= 0)
            return new SubmitOrderResult(SubmitOrderOutcome.InvalidQuantity, idempotencyKey);

        // Spec §1: one correlation id per submission ties events + audit rows.
        var correlationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var message = new OrderMessage(
            productId, quantity, idempotencyKey, now,
            UserId: userId,
            CorrelationId: correlationId,
            // Spec §5: the payment window starts at acceptance; worker persists it.
            PaymentDueAt: now.AddMinutes(paymentOptions.TimeoutMinutes));

        // T1 — fast reservation (also deduplicates by idempotency key).
        var reservation = await reservationGateway.TryReserveAsync(productId, quantity, idempotencyKey);
        if (reservation == ReservationResult.Duplicate)
            return new SubmitOrderResult(SubmitOrderOutcome.DuplicateRequest, idempotencyKey);
        if (reservation == ReservationResult.SoldOut)
            return new SubmitOrderResult(SubmitOrderOutcome.OutOfStock, idempotencyKey);

        // T1b — unknown product: seed the reservation tier from the DB once, then retry.
        if (reservation == ReservationResult.UnknownProduct)
        {
            var stock = await readModel.GetStockAsync(productId, ct);
            if (stock is null)
                return new SubmitOrderResult(SubmitOrderOutcome.ProductNotFound, idempotencyKey);

            await reservationGateway.SetStockAsync(productId, stock.Value);
            reservation = await reservationGateway.TryReserveAsync(productId, quantity, idempotencyKey);
            if (reservation == ReservationResult.SoldOut)
                return new SubmitOrderResult(SubmitOrderOutcome.OutOfStock, idempotencyKey);
        }

        if (reservation == ReservationResult.Reserved)
        {
            // T2 — ADR-019: the reservation fact is written to the transactional
            // outbox, NOT published to the broker. The row commits inside a
            // database transaction; if the write fails, the whole thing rolls
            // back and the order is not accepted — an accepted order can never
            // be missing its event.
            await transaction.BeginAsync(ct);
            try
            {
                // OrderId=0: the row does not exist yet; correlationId links
                // this event to order.created, which carries the real id.
                await outboxWriter.EnqueueAsync(
                    new InventoryReservedEvent(
                        Guid.NewGuid().ToString("D"), correlationId, "order-api",
                        DateTimeOffset.UtcNow, 0, productId, quantity, idempotencyKey),
                    OutboxEventWriter.OrderAggregate,
                    idempotencyKey,
                    ct);

                await transaction.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(ct);
                await reservationGateway.ReleaseReservationAsync(productId, quantity, idempotencyKey);
                logger.LogError(ex,
                    "inventory.reserved outbox write failed for {Key} — reservation released, order not accepted.",
                    idempotencyKey);
                return new SubmitOrderResult(SubmitOrderOutcome.SystemBusy, idempotencyKey);
            }

            // T2b — hand off to the fulfillment pipeline.
            if (await queueProducer.EnqueueAsync(message, ct))
            {
                return new SubmitOrderResult(SubmitOrderOutcome.Accepted, idempotencyKey);
            }

            // T2c — queue full -> backpressure: give the reservation back and shed
            // load. The committed inventory.reserved event is compensated by an
            // inventory.released event, so the event stream stays truthful.
            await reservationGateway.ReleaseReservationAsync(productId, quantity, idempotencyKey);
            await CompensateReservationAsync(productId, quantity, idempotencyKey, correlationId, ct);
            return new SubmitOrderResult(SubmitOrderOutcome.SystemBusy, idempotencyKey);
        }

        // T1c — reservation tier unavailable: fall back to the synchronous use case
        // (Phase 3 path; still idempotent thanks to the repository constraint).
        // The order row and its order.created event are written in one
        // transaction by the processor (ADR-019).
        logger.LogWarning(
            "Reservation tier unavailable ({Reservation}) — falling back to the synchronous path.",
            reservation);
        try
        {
            await processor.ProcessAsync(message, ct);
            return new SubmitOrderResult(SubmitOrderOutcome.CompletedSynchronously, idempotencyKey);
        }
        catch (StockDriftException)
        {
            return new SubmitOrderResult(SubmitOrderOutcome.StockDrift, idempotencyKey);
        }
    }

    /// <summary>
    /// Compensate a committed inventory.reserved event when the order could not
    /// be accepted after all. Best effort: a failure here is logged, never
    /// thrown — the caller must still get its SystemBusy answer.
    /// </summary>
    private async Task CompensateReservationAsync(
        int productId,
        int quantity,
        string idempotencyKey,
        Guid correlationId,
        CancellationToken ct)
    {
        try
        {
            await transaction.BeginAsync(ct);
            await outboxWriter.EnqueueAsync(
                new InventoryReleasedEvent(
                    Guid.NewGuid().ToString("D"), correlationId, "order-api",
                    DateTimeOffset.UtcNow, 0, productId, quantity),
                OutboxEventWriter.OrderAggregate,
                idempotencyKey,
                ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            logger.LogError(ex,
                "inventory.released compensation could not be recorded for {Key}.", idempotencyKey);
        }
    }
}
