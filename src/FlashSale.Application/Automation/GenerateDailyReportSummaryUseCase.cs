using FlashSale.Application.Persistence;
using Microsoft.Extensions.Logging;

namespace FlashSale.Application.Automation;

/// <summary>
/// Use case: retrieves PostgreSQL daily aggregates and delegates to IAiAutomationService
/// for executive synthesis and operational health categorization.
/// </summary>
public sealed class GenerateDailyReportSummaryUseCase(
    IDailyReportRepository reports,
    IAiAutomationService aiService,
    ReportingAutomationOptions options,
    ILogger<GenerateDailyReportSummaryUseCase> logger)
{
    public async Task<DailyReportAiSummaryResult> ExecuteAsync(
        DateTime? dateUtc = null,
        CancellationToken ct = default)
    {
        var targetDate = dateUtc ?? DateTime.UtcNow.Date;
        var (fromUtc, toUtc) = ReportWindow.ForDay(targetDate);

        var metrics = await reports.ComputeMetricsAsync(fromUtc, toUtc, options.TopProductCount, ct);

        logger.LogInformation(
            "Generating AI Daily Report Summary for {Date:yyyy-MM-dd}: Orders={Orders}, Revenue={Revenue}",
            targetDate, metrics.TotalOrders, metrics.Revenue);

        return await aiService.GenerateDailyReportSummaryAsync(metrics, targetDate, ct);
    }
}
