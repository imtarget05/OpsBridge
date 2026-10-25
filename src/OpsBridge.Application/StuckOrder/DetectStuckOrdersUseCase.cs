using Microsoft.Extensions.Logging;
using OpsBridge.Application.Common;
using OpsBridge.Application.Reconciliation;
using OpsBridge.Application.StuckOrder;
using OpsBridge.Domain.Reconciliation;
using OpsBridge.Domain.StuckOrder;

namespace OpsBridge.Application.UseCases;

public sealed class DetectStuckOrdersUseCase(
    IPurchaseOrderRepository poRepository,
    IGoodsReceiptRepository grRepository,
    ISupplierInvoiceRepository invoiceRepository,
    IStuckOrderAlertRepository stuckOrderAlertRepository,
    IUnitOfWork unitOfWork,
    ITimeProvider timeProvider,
    ILogger<DetectStuckOrdersUseCase> logger)
{
    public async Task<IReadOnlyList<StuckOrderDetectionResult>> ExecuteAsync(
        StuckOrderDetectionOptions options,
        CancellationToken ct = default)
    {
        var results = new List<StuckOrderDetectionResult>();
        var now = timeProvider.UtcNow;
        var slaThreshold = options.SlaThreshold;

        var purchaseOrders = await poRepository.ListAsync(500, ct);

        foreach (var po in purchaseOrders)
        {
            if (po.Status == PurchaseOrderStatus.FullyReceived || po.Status == PurchaseOrderStatus.Closed)
            {
                continue;
            }

            var receipts = await grRepository.GetByPurchaseOrderNumberAsync(po.OrderNumber, ct);
            var invoices = await invoiceRepository.GetByPurchaseOrderNumberAsync(po.OrderNumber, ct);

            var lastProgress = GetLastProgressTime(po, receipts, invoices);
            var timeSinceLastProgress = now - lastProgress;

            var isStuck = timeSinceLastProgress > slaThreshold;

            var severity = timeSinceLastProgress >= slaThreshold * 2
                ? StuckOrderSeverity.Critical
                : StuckOrderSeverity.Warning;

            var reason = BuildStuckReason(po, receipts, invoices, timeSinceLastProgress, slaThreshold);

            var result = new StuckOrderDetectionResult(
                isStuck,
                po.OrderNumber,
                po.SupplierCode,
                reason,
                severity,
                timeSinceLastProgress,
                slaThreshold);

            results.Add(result);

            if (isStuck)
            {
                await HandleStuckOrderAsync(po, result, options, ct);
            }
            else
            {
                await ClearResolvedStuckOrderAsync(po.Id, ct);
            }
        }

        return results;
    }

    private DateTimeOffset GetLastProgressTime(
        PurchaseOrder po,
        IReadOnlyList<GoodsReceipt> receipts,
        IReadOnlyList<SupplierInvoice> invoices)
    {
        var times = new List<DateTimeOffset> { po.OrderedAt };

        if (receipts.Count > 0)
        {
            times.Add(receipts.Max(r => r.ReceivedAt));
        }

        if (invoices.Count > 0)
        {
            times.Add(invoices.Max(i => i.InvoiceDate));
        }

        return times.Max();
    }

    private string BuildStuckReason(
        PurchaseOrder po,
        IReadOnlyList<GoodsReceipt> receipts,
        IReadOnlyList<SupplierInvoice> invoices,
        TimeSpan timeSinceLastProgress,
        TimeSpan slaThreshold)
    {
        var receivedQty = receipts.SelectMany(r => r.Lines).Sum(l => l.ReceivedQuantity);
        var orderedQty = po.Lines.Sum(l => l.OrderedQuantity);
        var invoicedQty = invoices.SelectMany(i => i.Lines).Sum(l => l.InvoicedQuantity);

        var parts = new List<string>
        {
            $"PO {po.OrderNumber} has not progressed for {timeSinceLastProgress.TotalHours:F1} hours (SLA: {slaThreshold.TotalHours:F1}h).",
            $"Ordered: {orderedQty}, Received: {receivedQty}, Invoiced: {invoicedQty}."
        };

        if (receivedQty == 0)
        {
            parts.Add("No goods receipt recorded yet.");
        }
        else if (receivedQty < orderedQty)
        {
            parts.Add($"Partial receipt: {receivedQty}/{orderedQty} received.");
        }

        if (invoices.Count == 0)
        {
            parts.Add("No invoice received.");
        }

        return string.Join(" ", parts);
    }

    private async Task HandleStuckOrderAsync(
        PurchaseOrder po,
        StuckOrderDetectionResult result,
        StuckOrderDetectionOptions options,
        CancellationToken ct)
    {
        var existingAlert = await stuckOrderAlertRepository.GetByPurchaseOrderIdAsync(po.Id, ct);

        if (existingAlert == null)
        {
            var newAlert = new StuckOrderAlert
            {
                PurchaseOrderId = po.Id,
                PurchaseOrderNumber = po.OrderNumber,
                SupplierCode = po.SupplierCode,
                Reason = result.Reason,
                Severity = result.Severity,
                Status = StuckOrderStatus.Open,
                DetectedAt = timeProvider.UtcNow
            };

            await using var tx = await unitOfWork.BeginTransactionAsync(ct);
            try
            {
                await stuckOrderAlertRepository.AddAsync(newAlert, ct);
                await tx.CommitAsync(ct);
                logger.LogWarning("Stuck order detected: {PoNumber} - {Reason}", po.OrderNumber, result.Reason);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "Failed to create stuck order alert for PO {PoNumber}", po.OrderNumber);
            }
        }
        else
        {
            existingAlert.Reason = result.Reason;
            existingAlert.Severity = result.Severity;
            existingAlert.LastRetryAt = timeProvider.UtcNow;
            existingAlert.RetryCount++;

            if (existingAlert.RetryCount >= options.MaxRetries && !existingAlert.IsDeadLettered)
            {
                existingAlert.IsDeadLettered = true;
                existingAlert.DeadLetterReason = $"Max retries ({options.MaxRetries}) exceeded";
                existingAlert.Status = StuckOrderStatus.Open;
                logger.LogError("Stuck order alert dead-lettered after {RetryCount} retries: {PoNumber}",
                    existingAlert.RetryCount, po.OrderNumber);
            }

            await using var tx = await unitOfWork.BeginTransactionAsync(ct);
            try
            {
                await stuckOrderAlertRepository.UpdateAsync(existingAlert, ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "Failed to update stuck order alert for PO {PoNumber}", po.OrderNumber);
            }
        }
    }

    private async Task ClearResolvedStuckOrderAsync(Guid purchaseOrderId, CancellationToken ct)
    {
        var existingAlert = await stuckOrderAlertRepository.GetByPurchaseOrderIdAsync(purchaseOrderId, ct);

        if (existingAlert != null && existingAlert.Status == StuckOrderStatus.Open)
        {
            existingAlert.Status = StuckOrderStatus.Resolved;
            existingAlert.ResolvedAt = timeProvider.UtcNow;
            existingAlert.ResolutionNotes = "Order progressed - no longer stuck";

            await using var tx = await unitOfWork.BeginTransactionAsync(ct);
            try
            {
                await stuckOrderAlertRepository.UpdateAsync(existingAlert, ct);
                await tx.CommitAsync(ct);
                logger.LogInformation("Stuck order resolved (progress detected): {PoId}", purchaseOrderId);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(ct);
                logger.LogError(ex, "Failed to resolve stuck order alert for PO {PoId}", purchaseOrderId);
            }
        }
    }
}