using FlashSale.Application.Inventory;
using FlashSale.Application.Outbox;
using FlashSale.Application.Persistence;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Events;
using Microsoft.Extensions.Logging;

namespace FlashSale.Application.Automation;

/// <summary>Outcome of one timeout scan (spec §5) — feeds the endpoint/worker logs.</summary>
public sealed record PaymentScanResult(int Scanned, int Reminded, int Cancelled);

/// <summary>
/// Abandoned-payment recovery (spec §5 + §4 cancel branch): scan
/// PENDING_PAYMENT orders → remind inside grace → past grace cancel, release
/// inventory (DB transactional, Redis best-effort) and publish payment.expired /
/// order.cancelled / inventory.released. Every scan writes exactly one
/// AutomationRun audit row (spec §11), success or failure.
///
/// <para>
/// ADR-019: the cancellation and its three events are written in ONE
/// transaction. Before this change the scan published straight to the broker
/// in a swallowed <c>try/catch</c> — a dual-write: the order could be
/// cancelled with no event, or the event could claim a cancellation that was
/// rolled back. Now the events go through the transactional outbox, so they
/// are published by the dispatcher, retried on failure, and deduped by the
/// consumer on the stable <c>event_id</c>.
/// </para>
/// </summary>
public sealed class PaymentAutomationUseCase(
    IOrderReadModel readModel,
    IPaymentRepository payments,
    IStockReservationGateway reservationGateway,
    IAutomationRunRepository runs,
    IWriteTransaction transaction,
    OutboxEventWriter outboxWriter,
    PaymentAutomationOptions options,
    ILogger<PaymentAutomationUseCase> logger)
{
    /// <summary>Upper bound per scan so one run can never be unbounded (§12).</summary>
    public const int BatchSize = 200;

    public async Task<PaymentScanResult> ExecuteAsync(string triggerType, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new AutomationRun
        {
            WorkflowName = AutomationWorkflow.PaymentTimeout.ToString(),
            TriggerType = triggerType,
            TriggerId = Guid.NewGuid().ToString("N"),
            Status = AutomationRunStatus.Running,
            StartedAt = now,
            CorrelationId = Guid.NewGuid()
        };
        await runs.CreateAsync(run, ct);

        var reminded = 0;
        var cancelled = 0;
        var scanned = 0;

        try
        {
            var due = await readModel.GetPendingPaymentOrdersAsync(BatchSize, ct);
            scanned = due.Count;

            foreach (var order in due)
            {
                ct.ThrowIfCancellationRequested();
                var decision = PaymentTimeoutRule.Evaluate(
                    order.PaymentDueAt,
                    order.PaymentReminderCount,
                    options.MaxPaymentReminders,
                    options.GracePeriodMinutes,
                    now);

                switch (decision)
                {
                    case PaymentTimeoutRule.Decision.SendReminder:
                        if (await payments.IncrementReminderAsync(order.OrderId, ct) > 0)
                        {
                            reminded++;
                            // Notification channel (email/webhook/n8n) is PLANNED —
                            // until wired, the reminder = structured log + audit row.
                            logger.LogInformation(
                                "Payment reminder {Count}/{Max} for order {OrderId} (key {Key}).",
                                order.PaymentReminderCount + 1, options.MaxPaymentReminders,
                                order.OrderId, order.IdempotencyKey);
                        }
                        break;

                    case PaymentTimeoutRule.Decision.Cancel:
                        if (await CancelWithEventsAsync(order, run, ct))
                        {
                            cancelled++;
                            logger.LogInformation(
                                "Order {OrderId} cancelled after payment timeout — stock released (product {ProductId}, qty {Quantity}).",
                                order.OrderId, order.ProductId, order.Quantity);
                        }
                        break;
                }
            }

            run.Status = AutomationRunStatus.Success;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ResultSummary = $"scanned={scanned} reminded={reminded} cancelled={cancelled}";
        }
        catch (Exception ex)
        {
            // The cancellation and its events are rolled back together: a
            // cancelled order with no event (or an event for a cancellation that
            // never happened) is exactly the inconsistency the outbox exists to
            // remove.
            await transaction.RollbackAsync(ct);

            run.Status = AutomationRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ErrorCode = ex.GetType().Name;
            run.ErrorMessage = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
            logger.LogError(ex, "Payment timeout scan failed after {Scanned} rows.", scanned);
        }

        await runs.UpdateAsync(run, ct);
        return new PaymentScanResult(scanned, reminded, cancelled);
    }

    /// <summary>
    /// Cancel one order and write its three events in ONE transaction
    /// (ADR-019). Returns false when the guarded UPDATE matched no row — the
    /// order was already settled by someone else, so there is nothing to
    /// cancel and nothing to publish.
    /// </summary>
    private async Task<bool> CancelWithEventsAsync(
        PendingPaymentView order,
        AutomationRun run,
        CancellationToken ct)
    {
        await transaction.BeginAsync(ct);

        if (!await payments.CancelAndReleaseStockAsync(order.OrderId, ct))
        {
            // No transition (the status guard rejected it): nothing was
            // written, so there is nothing to publish.
            await transaction.CommitAsync(ct);
            return false;
        }

        var cid = run.CorrelationId;
        await outboxWriter.EnqueueAsync(
            new PaymentExpiredEvent(
                Guid.NewGuid().ToString("D"), cid, "automation/payment-timeout",
                DateTimeOffset.UtcNow, order.OrderId, options.TimeoutMinutes),
            OutboxEventWriter.OrderAggregate,
            order.OrderId.ToString(),
            ct);
        await outboxWriter.EnqueueAsync(
            new OrderCancelledEvent(
                Guid.NewGuid().ToString("D"), cid, "automation/payment-timeout",
                DateTimeOffset.UtcNow, order.OrderId, "payment_timeout"),
            OutboxEventWriter.OrderAggregate,
            order.OrderId.ToString(),
            ct);
        await outboxWriter.EnqueueAsync(
            new InventoryReleasedEvent(
                Guid.NewGuid().ToString("D"), cid, "automation/payment-timeout",
                DateTimeOffset.UtcNow, order.OrderId, order.ProductId, order.Quantity),
            OutboxEventWriter.OrderAggregate,
            order.OrderId.ToString(),
            ct);

        // The cancellation and its events become visible together.
        await transaction.CommitAsync(ct);

        // The Redis reservation tier is a cache, not the truth: it is released
        // AFTER the commit, so a cache failure can never undo the committed
        // cancellation (which would be the worse inconsistency). A failure here
        // is logged and the scan continues — the DB already holds the truth.
        try
        {
            await reservationGateway.ReleaseReservationAsync(
                order.ProductId, order.Quantity, order.IdempotencyKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Redis reservation release failed for order {OrderId} (product {ProductId}); " +
                "the cancellation and its events are committed.",
                order.OrderId, order.ProductId);
        }

        return true;
    }
}
