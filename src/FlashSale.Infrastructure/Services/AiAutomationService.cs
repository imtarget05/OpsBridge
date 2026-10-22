using FlashSale.Application.Automation;
using FlashSale.Application.Persistence;
using Microsoft.Extensions.Logging;

namespace FlashSale.Infrastructure.Services;

/// <summary>
/// Infrastructure adapter implementing IAiAutomationService.
/// Computes replenishment targets and executive synthesis strictly from PostgreSQL data.
/// Deterministic and fail-safe: operates reliably without external internet dependency.
/// </summary>
public sealed class AiAutomationService(ILogger<AiAutomationService> logger) : IAiAutomationService
{
    public Task<ReorderRecommendationResult> GenerateReorderRecommendationAsync(
        ReorderRecommendationRequest request,
        CancellationToken ct = default)
    {
        var dailyVelocity = request.SalesVelocityLast7Days / 7.0;
        var leadTimeDemand = (int)Math.Ceiling(dailyVelocity * request.SupplierLeadTimeDays);

        var safetyStock = Math.Max(request.ReorderThreshold, (int)Math.Ceiling(leadTimeDemand * 0.5));

        var targetLevel = leadTimeDemand + safetyStock;
        var recommendedQty = Math.Max(0, targetLevel - request.CurrentStock);

        string rationale;
        if (recommendedQty == 0)
        {
            rationale = $"Tồn kho khả dụng ({request.CurrentStock}) vẫn đáp ứng đủ định mức an toàn ({targetLevel}) cho lead time {request.SupplierLeadTimeDays} ngày. Chưa cần lập đơn nhập bổ sung.";
        }
        else
        {
            rationale = $"Tốc độ bán trung bình {dailyVelocity:F1} sp/ngày; nhu cầu trong {request.SupplierLeadTimeDays} ngày lead time là {leadTimeDemand} sp. Cần nhập tối thiểu {recommendedQty} sp để đưa tồn kho lên ngưỡng an toàn {targetLevel} sp (kèm dự phòng {safetyStock} sp).";
        }

        logger.LogInformation(
            "Generated reorder recommendation for product {ProductId}: RecommendedQty={Qty}, SafetyStock={Safety}",
            request.ProductId, recommendedQty, safetyStock);

        return Task.FromResult(new ReorderRecommendationResult(
            ProductId: request.ProductId,
            RecommendedReorderQuantity: recommendedQty,
            SafetyStock: safetyStock,
            Rationale: rationale,
            Source: "heuristic_eoq",
            IsSimulated: true));
    }

    public Task<DailyReportAiSummaryResult> GenerateDailyReportSummaryAsync(
        DailyReportMetrics metrics,
        DateTime reportDate,
        CancellationToken ct = default)
    {
        var total = metrics.TotalOrders;
        var confirmed = metrics.ConfirmedOrders;
        var cancelled = metrics.CancelledOrders;
        var failed = metrics.FailedPayments;
        var revenue = metrics.Revenue;

        var successRate = total > 0 ? (confirmed / (double)total) * 100 : 0.0;
        var failRate = total > 0 ? (failed / (double)total) * 100 : 0.0;

        string healthStatus;
        if (failRate > 15.0 || (total > 5 && successRate < 60.0))
        {
            healthStatus = "Critical";
        }
        else if (failRate > 5.0 || (total > 5 && successRate < 80.0))
        {
            healthStatus = "AttentionRequired";
        }
        else
        {
            healthStatus = "Healthy";
        }

        var execSummary = $"Báo cáo ngày {reportDate:yyyy-MM-dd}: Tổng {total} đơn hàng, {confirmed} đơn thành công ({successRate:F1}%), doanh thu ghi nhận {revenue:N0} VNĐ. Trạng thái vận hành: {healthStatus}.";

        var insights = $"Phát hiện {cancelled} đơn hủy và {failed} đơn thanh toán quá hạn/lỗi ({failRate:F1}%).";
        if (metrics.TopProducts.Count > 0)
        {
            var top = metrics.TopProducts[0];
            insights += $" Mặt hàng bán chạy nhất: {top.Name} (SL: {top.Quantity}, Doanh thu: {top.Revenue:N0} VNĐ).";
        }

        logger.LogInformation("Generated Daily Report AI Summary for {Date:yyyy-MM-dd}: Health={Health}", reportDate, healthStatus);

        return Task.FromResult(new DailyReportAiSummaryResult(
            ReportDate: reportDate,
            ExecutiveSummary: execSummary,
            KeyInsights: insights,
            HealthStatus: healthStatus,
            Source: "heuristic_engine",
            IsSimulated: true));
    }
}
