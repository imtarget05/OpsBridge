using FlashSale.Application.Automation;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Spec §16/§17-B: reduce inventory → threshold detected → alert → notification
/// hook, plus the dedupe guarantee that keeps a periodic scan from spamming.
/// </summary>
[Collection("flashsale")]
public sealed class LowStockAutomationTests(FlashSaleFixture fx)
{
    private static InventoryAutomationOptions Options(int defaultThreshold = 5) =>
        new() { DefaultReorderThreshold = defaultThreshold, ScanIntervalSeconds = 60 };

    private static async Task SetStockAsync(FlashSaleFixture fx, int productId, int stock, int? threshold = null)
    {
        await using var db = fx.CreateDbContext();
        var product = await db.Products.SingleAsync(p => p.Id == productId);
        product.AvailableStock = stock;
        if (threshold is not null) product.ReorderThreshold = threshold.Value;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Scan_DetectsLowStock_CreatesAlert_PublishesEvent_WritesAudit()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        await SetStockAsync(fx, productId, stock: 5); // == threshold(5) => LOW (inclusive)

        await using var db = fx.CreateDbContext();
        var useCase = fx.CreateLowStockAlertUseCase(db, Options());

        var result = await useCase.ExecuteAsync("manual", CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.AlertsCreated);
        Assert.Equal([productId], result.ProductIds);

        var alert = await db.StockAlerts.SingleAsync(a => a.ProductId == productId);
        Assert.Equal(StockAlertStatus.Open, alert.Status);
        Assert.Equal(5, alert.AvailableStock);
        Assert.Equal(5, alert.ReorderThreshold);

        // ADR-019: the event is NOT published directly any more — it is written
        // to the transactional outbox in the same transaction as the alert row.
        var outboxTypes = await db.OutboxMessages.Select(m => m.EventType).ToListAsync();
        Assert.Contains("inventory.low_stock", outboxTypes);
        var outboxRow = await db.OutboxMessages.SingleAsync(m => m.EventType == "inventory.low_stock");
        Assert.Equal("inventory", outboxRow.AggregateType);
        Assert.Equal(productId.ToString(), outboxRow.AggregateId);

        var run = await db.AutomationRuns
            .Where(r => r.WorkflowName == AutomationWorkflow.InventoryAutomation.ToString())
            .OrderByDescending(r => r.Id).FirstAsync();
        Assert.Equal(AutomationRunStatus.Success, run.Status);
        Assert.Contains("alerts_created=1", run.ResultSummary);
    }

    [Fact]
    public async Task Scan_IsDeduped_ByOpenAlert_SecondRunCreatesNothing()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        await SetStockAsync(fx, productId, stock: 3);

        await using var db = fx.CreateDbContext();
        var useCase = fx.CreateLowStockAlertUseCase(db, Options());

        var first = await useCase.ExecuteAsync("manual", CancellationToken.None);
        var second = await useCase.ExecuteAsync("timer", CancellationToken.None);

        Assert.Equal(1, first.AlertsCreated);
        Assert.Equal(1, second.Scanned);     // still below threshold
        Assert.Equal(0, second.AlertsCreated); // deduped: one Open alert per product

        Assert.Equal(1, await db.StockAlerts.CountAsync(a => a.ProductId == productId));

        // ADR-019: exactly ONE event — the deduped second scan publishes
        // nothing, because no alert was created.
        Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.EventType == "inventory.low_stock"));
    }

    [Fact]
    public async Task Scan_HealthyStock_CreatesNothing()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10); // 10 > default 5
        await SetStockAsync(fx, productId, stock: 6);           // one above threshold

        await using var db = fx.CreateDbContext();
        var useCase = fx.CreateLowStockAlertUseCase(db, Options());

        var result = await useCase.ExecuteAsync("manual", CancellationToken.None);

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.AlertsCreated);
        Assert.Equal(0, await db.StockAlerts.CountAsync());
        Assert.Equal(0, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task PerProductThreshold_OverridesDefault()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        // Product wants a reorder point of 8; stock 8 => LOW although 8 > default 5.
        await SetStockAsync(fx, productId, stock: 8, threshold: 8);

        await using var db = fx.CreateDbContext();
        var useCase = fx.CreateLowStockAlertUseCase(db, Options(defaultThreshold: 5));

        var result = await useCase.ExecuteAsync("manual", CancellationToken.None);

        Assert.Equal(1, result.AlertsCreated);
        var alert = await db.StockAlerts.SingleAsync(a => a.ProductId == productId);
        Assert.Equal(8, alert.ReorderThreshold);
    }
}