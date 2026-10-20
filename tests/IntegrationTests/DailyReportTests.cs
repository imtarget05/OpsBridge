using FlashSale.Application.Outbox;
using FlashSale.Application.Automation;
using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Application.Persistence;
using FlashSale.Domain.Automation;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Spec §7/§17-C: trigger → metrics from the database → stored report, plus the
/// upsert guarantee (one row per day) and the audit row.
/// </summary>
[Collection("flashsale")]
public sealed class DailyReportTests(FlashSaleFixture fx)
{
    private static DailyReportUseCase UseCase(AppDbContext db) => new(
        new DailyReportRepository(db),
        new StockAlertRepository(db),
        new AutomationRunRepository(db),
        new InventoryAutomationOptions { DefaultReorderThreshold = 5 },
        new ReportingAutomationOptions { RunAtHourUtc = 0, TopProductCount = 5 },
        NullLogger<DailyReportUseCase>.Instance);

    private static OrderMessage Msg(int productId, string key, DateTimeOffset? due, int qty = 1) =>
        new(productId, qty, key, DateTimeOffset.UtcNow, PaymentDueAt: due, CorrelationId: Guid.NewGuid());

    private static async Task<int> PersistAsync(AppDbContext db, OrderMessage msg)
    {
        var repo = new OrderRepository(db);
        await new OrderProcessor(
                repo,
                new EfWriteTransaction(db),
                new OutboxEventWriter(new OutboxRepository(db)),
                NullLogger<OrderProcessor>.Instance)
            .ProcessAsync(msg, CancellationToken.None);
        return await repo.GetOrderIdAsync(msg.IdempotencyKey, CancellationToken.None) ?? 0;
    }

    [Fact]
    public async Task Report_ComputesAllFiguresFromDatabase()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 100);

        // price from the fixture's product (seed uses the default price — set one)
        await using (var seed = fx.CreateDbContext())
        {
            var product = await seed.Products.SingleAsync(p => p.Id == productId);
            product.FlashSalePrice = 50m;
            await seed.SaveChangesAsync();
        }

        var due = DateTimeOffset.UtcNow.AddMinutes(10);

        // confirmed ×2 orders (1 unit + 2 units) → revenue 150
        await using (var db = fx.CreateDbContext())
        {
            var payments = new PaymentRepository(db);
            var o1 = await PersistAsync(db, Msg(productId, "rep-paid-1", due));
            await payments.MarkPaidAsync(o1, CancellationToken.None);
            var o2 = await PersistAsync(db, Msg(productId, "rep-paid-2", due, qty: 2));
            await payments.MarkPaidAsync(o2, CancellationToken.None);
        }

        // cancelled (payment timeout path)
        await using (var db = fx.CreateDbContext())
        {
            var o3 = await PersistAsync(db, Msg(productId, "rep-cancel", DateTimeOffset.UtcNow.AddMinutes(-30)));
            await new PaymentRepository(db).CancelAndReleaseStockAsync(o3, CancellationToken.None);
        }

        // payment failed (stays pending)
        await using (var db = fx.CreateDbContext())
        {
            var o4 = await PersistAsync(db, Msg(productId, "rep-failed", due));
            await new PaymentRepository(db).MarkPaymentFailedAsync(o4, CancellationToken.None);
        }

        await using var reportDb = fx.CreateDbContext();
        var report = await UseCase(reportDb).ExecuteAsync(DateTime.UtcNow, "manual", CancellationToken.None);

        Assert.Equal(4, report.TotalOrders);
        Assert.Equal(2, report.ConfirmedOrders);
        Assert.Equal(1, report.CancelledOrders);
        Assert.Equal(1, report.FailedPayments);
        Assert.Equal(150m, report.Revenue);           // (1+2) units × 50
        Assert.Equal(75m, report.AverageOrderValue);  // 150 / 2 confirmed
        Assert.Equal(0, report.RefundCount);          // no refund domain (documented)

        var top = JsonSerializer.Deserialize<List<TopProductView>>(report.TopProductsJson)!;
        Assert.Single(top);
        Assert.Equal(3, top[0].Quantity);             // 1 + 2 confirmed units
        Assert.Equal(150m, top[0].Revenue);

        var low = JsonSerializer.Deserialize<List<FlashSale.Application.Persistence.LowStockProductView>>(
            report.LowStockProductsJson)!;
        Assert.Empty(low);                            // stock 100 → healthy

        var run = await reportDb.AutomationRuns
            .Where(r => r.WorkflowName == AutomationWorkflow.DailyReport.ToString())
            .OrderByDescending(r => r.Id).FirstAsync();
        Assert.Equal(AutomationRunStatus.Success, run.Status);
        Assert.Contains("orders=4", run.ResultSummary);
    }

    [Fact]
    public async Task RerunSameDay_UpsertsSingleRow()
    {
        await fx.ResetDatabaseAsync(stock: 10);
        await using var db = fx.CreateDbContext();

        var first = await UseCase(db).ExecuteAsync(DateTime.UtcNow, "manual", CancellationToken.None);
        var second = await UseCase(db).ExecuteAsync(DateTime.UtcNow, "manual", CancellationToken.None);

        Assert.Equal(first.Id, second.Id);                    // same row
        Assert.Equal(1, await db.DailyReports.CountAsync());  // one row per day
        Assert.Equal(0m, second.Revenue);                     // no confirmed orders here
    }

    [Fact]
    public async Task Latest_ReturnsMostRecentReport()
    {
        await fx.ResetDatabaseAsync(stock: 10);
        await using var db = fx.CreateDbContext();

        await UseCase(db).ExecuteAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "manual", CancellationToken.None);
        await UseCase(db).ExecuteAsync(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), "manual", CancellationToken.None);

        var latest = await new DailyReportRepository(db).GetLatestAsync(CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc), latest!.ReportDate);
    }
}
