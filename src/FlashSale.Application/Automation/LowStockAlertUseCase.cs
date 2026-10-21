using FlashSale.Application.Outbox;
using FlashSale.Application.Persistence;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Events;
using FlashSale.Domain.Inventory;
using Microsoft.Extensions.Logging;

namespace FlashSale.Application.Automation;

/// <summary>
/// Inventory low-stock automation (spec §6): evaluate REAL stock values against
/// the reorder threshold, raise a deduplicated LOW_STOCK alert, publish
/// inventory.low_stock and record one AutomationRun per scan. The AI layer is
/// never involved — stock values are database truth only (spec §6 note).
///
/// <para>
/// ADR-019: the alert row and its <c>inventory.low_stock</c> event are
/// written in ONE transaction. Before this change the scan published straight
/// to the broker in a swallowed <c>try/catch</c>, so an alert could exist
/// with no event and an event could describe an alert that was never created.
/// The event now travels through the transactional outbox.
/// </para>
/// </summary>
public sealed class LowStockAlertUseCase(
    IStockAlertRepository alerts,
    IAutomationRunRepository runs,
    IWriteTransaction transaction,
    OutboxEventWriter outboxWriter,
    InventoryAutomationOptions options,
    ILogger<LowStockAlertUseCase> logger)
{
    public const int BatchSize = 200;

    public async Task<LowStockScanResult> ExecuteAsync(string triggerType, CancellationToken ct = default)
    {
        var run = new AutomationRun
        {
            WorkflowName = AutomationWorkflow.InventoryAutomation.ToString(),
            TriggerType = triggerType,
            TriggerId = Guid.NewGuid().ToString("N"),
            Status = AutomationRunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid()
        };
        await runs.CreateAsync(run, ct);

        var created = new List<int>();
        var scanned = 0;

        try
        {
            var products = await alerts.GetLowStockProductsAsync(
                options.DefaultReorderThreshold, BatchSize, ct);
            scanned = products.Count;

            foreach (var product in products)
            {
                ct.ThrowIfCancellationRequested();

                var threshold = product.ReorderThreshold > 0
                    ? product.ReorderThreshold
                    : options.DefaultReorderThreshold;

                // Rule is re-checked in-process (defence in depth): the SQL filter
                // and the rule must agree, and the rule is the tested artifact.
                if (!LowStockRule.IsLowStock(product.AvailableStock, threshold)) continue;

                // The alert row and its event commit together (ADR-019).
                await transaction.BeginAsync(ct);

                var alert = await alerts.TryCreateAsync(new StockAlert
                {
                    ProductId = product.ProductId,
                    AvailableStock = product.AvailableStock,
                    ReorderThreshold = threshold,
                    Status = StockAlertStatus.Open,
                    CreatedAt = DateTimeOffset.UtcNow,
                    CorrelationId = run.CorrelationId
                }, ct);

                if (alert is null)
                {
                    logger.LogDebug(
                        "Low stock re-detected for product {ProductId} (stock {Stock} <= {Threshold}) — alert already open.",
                        product.ProductId, product.AvailableStock, threshold);

                    // Deduped: one Open alert per product. Nothing was written,
                    // so nothing is published.
                    await transaction.CommitAsync(ct);
                    continue;
                }

                await outboxWriter.EnqueueAsync(
                    new InventoryLowStockEvent(
                        Guid.NewGuid().ToString("D"), run.CorrelationId, "automation/inventory",
                        DateTimeOffset.UtcNow, product.ProductId, product.AvailableStock, threshold),
                    OutboxEventWriter.InventoryAggregate,
                    product.ProductId.ToString(),
                    ct);

                await transaction.CommitAsync(ct);

                created.Add(product.ProductId);
                logger.LogWarning(
                    "LOW_STOCK alert #{AlertId}: product {ProductId} stock {Stock} <= threshold {Threshold} (notification channel PLANNED: dashboard/email).",
                    alert.Id, product.ProductId, product.AvailableStock, threshold);
            }

            run.Status = AutomationRunStatus.Success;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ResultSummary = $"scanned={scanned} alerts_created={created.Count}" +
                (created.Count > 0 ? $" products=[{string.Join(",", created)}]" : string.Empty);
        }
        catch (Exception ex)
        {
            // The alert and its event roll back together: an open alert with no
            // event (or an event for an alert that was rolled back) is exactly
            // the inconsistency the outbox exists to remove.
            await transaction.RollbackAsync(ct);

            run.Status = AutomationRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ErrorCode = ex.GetType().Name;
            run.ErrorMessage = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
            logger.LogError(ex, "Low-stock scan failed after {Scanned} rows.", scanned);
        }

        await runs.UpdateAsync(run, ct);
        return new LowStockScanResult(scanned, created.Count, created);
    }
}
