using FlashSale.Application.Auth;
using FlashSale.Application.Outbox;
using FlashSale.Application.Persistence;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Events;
using Microsoft.Extensions.Logging;

namespace FlashSale.Application.Automation;

/// <summary>Payment outcome accepted by the simulation endpoint (spec §4 paid? branch).</summary>
public enum PaymentOutcome
{
    Completed,
    Failed
}

public sealed record RecordPaymentResult(bool Found, bool Transitioned)
{
    /// <summary>
    /// The order exists, but the caller is neither its owner nor Staff/Admin.
    /// A third state rather than folding it into "not found", so the endpoint can
    /// answer 403 honestly instead of pretending the order does not exist.
    /// </summary>
    public bool Forbidden { get; init; }
}

/// <summary>
/// Records a payment attempt against a PENDING_PAYMENT order (spec §4):
/// completed → Confirmed (+ payment.completed + order.confirmed events);
/// failed    → stays pending (+ payment.failed event). Status guards make the
/// transition exactly-once; every call writes an AutomationRun audit row.
/// This is a SIMULATED gateway — no real processor is integrated (§19 not
/// claimed otherwise).
///
/// <para>
/// ADR-019: the status transition and the events are written in ONE
/// transaction. The events go into the transactional outbox, so a confirmed
/// order can never be missing its <c>payment.completed</c> event.
/// </para>
/// </summary>
public sealed class RecordPaymentUseCase(
    IPaymentRepository payments,
    IOrderReadModel readModel,
    IAutomationRunRepository runs,
    IWriteTransaction transaction,
    OutboxEventWriter outboxWriter,
    ILogger<RecordPaymentUseCase> logger)
{
    /// <summary>
    /// Recording a payment drives an order to
    /// <c>Confirmed</c> and releases revenue-side events, so it is a
    /// STATE-MUTATING, order-scoped action: the caller must own the order (or be
    /// Staff/Admin) or nothing is written — not even the audit row.
    /// </summary>
    /// <remarks>
    /// An anonymous order (UserId = null) is owned by nobody, so only Staff/Admin
    /// can settle it. A caller whose <c>sub</c> does not parse is treated as
    /// owning nothing rather than owning every anonymous order.
    /// </remarks>
    public async Task<RecordPaymentResult> ExecuteAsync(
        string idempotencyKey,
        PaymentOutcome outcome,
        CallerIdentity caller,
        CancellationToken ct = default)
    {
        var order = await payments.GetByKeyAsync(idempotencyKey, ct);
        if (order is null)
            return new RecordPaymentResult(Found: false, Transitioned: false);

        if (!caller.IsStaffOrAdmin && (caller.UserId is null || order.UserId != caller.UserId))
        {
            logger.LogWarning(
                "Payment refused for order {OrderId}: caller {CallerId} is not the owner.",
                order.OrderId, caller.UserId);
            return new RecordPaymentResult(Found: true, Transitioned: false) { Forbidden = true };
        }

        var run = new AutomationRun
        {
            WorkflowName = AutomationWorkflow.OrderProcessing.ToString(),
            TriggerType = "api",
            TriggerId = $"pay:{idempotencyKey}",
            Status = AutomationRunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid()
        };
        await runs.CreateAsync(run, ct);

        var transitioned = false;
        try
        {
            await transaction.BeginAsync(ct);

            if (outcome == PaymentOutcome.Completed)
            {
                transitioned = await payments.MarkPaidAsync(order.OrderId, ct);
                if (transitioned)
                {
                    var amount = await ComputeAmountAsync(order, ct);
                    await outboxWriter.EnqueueAsync(
                        new PaymentCompletedEvent(
                            Guid.NewGuid().ToString("D"), run.CorrelationId, "order-api/pay",
                            DateTimeOffset.UtcNow, order.OrderId, amount, "simulation"),
                        OutboxEventWriter.OrderAggregate,
                        order.OrderId.ToString(),
                        ct);
                    await outboxWriter.EnqueueAsync(
                        new OrderConfirmedEvent(
                            Guid.NewGuid().ToString("D"), run.CorrelationId, "order-api/pay",
                            DateTimeOffset.UtcNow, order.OrderId),
                        OutboxEventWriter.OrderAggregate,
                        order.OrderId.ToString(),
                        ct);
                }
            }
            else
            {
                transitioned = await payments.MarkPaymentFailedAsync(order.OrderId, ct);
                if (transitioned)
                {
                    await outboxWriter.EnqueueAsync(
                        new PaymentFailedEvent(
                            Guid.NewGuid().ToString("D"), run.CorrelationId, "order-api/pay",
                            DateTimeOffset.UtcNow, order.OrderId, "simulation_failed"),
                        OutboxEventWriter.OrderAggregate,
                        order.OrderId.ToString(),
                        ct);
                }
            }

            // Status change + events commit together (ADR-019).
            await transaction.CommitAsync(ct);

            run.Status = AutomationRunStatus.Success;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ResultSummary = transitioned
                ? $"payment_{outcome.ToString().ToLowerInvariant()} order={order.OrderId}"
                : $"no_transition (status={order.Status}) order={order.OrderId}";
        }
        catch (Exception ex)
        {
            // The business change is rolled back with its events: a half-paid
            // order must not exist, and a payment.completed event must never
            // describe a state that was rolled back.
            await transaction.RollbackAsync(ct);

            run.Status = AutomationRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ErrorCode = ex.GetType().Name;
            run.ErrorMessage = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
            logger.LogError(ex, "Record payment failed for order {OrderId}.", order.OrderId);
        }

        await runs.UpdateAsync(run, ct);
        return new RecordPaymentResult(Found: true, Transitioned: transitioned);
    }

    private async Task<decimal> ComputeAmountAsync(PaymentOrderView order, CancellationToken ct)
    {
        var product = await readModel.GetProductAsync(order.ProductId, ct);
        return product?.FlashSalePrice * order.Quantity ?? 0m;
    }
}
