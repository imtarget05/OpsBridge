using FlashSale.Application.Persistence;

namespace FlashSale.Application.Automation;

public sealed record ReorderRecommendationRequest(
    int ProductId,
    string ProductName,
    int CurrentStock,
    int ReorderThreshold,
    int SalesVelocityLast7Days,
    int SupplierLeadTimeDays);

public sealed record ReorderRecommendationResult(
    int ProductId,
    int RecommendedReorderQuantity,
    int SafetyStock,
    string Rationale,
    string Source,
    bool IsSimulated);

public sealed record DailyReportAiSummaryResult(
    DateTime ReportDate,
    string ExecutiveSummary,
    string KeyInsights,
    string HealthStatus,
    string Source,
    bool IsSimulated);

/// <summary>
/// Port for AI automation capabilities: inventory replenishment recommendations
/// and daily business report executive summaries.
/// Follows Clean Architecture: Domain/Application declares contracts; Infrastructure provides adapters.
/// </summary>
public interface IAiAutomationService
{
    Task<ReorderRecommendationResult> GenerateReorderRecommendationAsync(
        ReorderRecommendationRequest request,
        CancellationToken ct = default);

    Task<DailyReportAiSummaryResult> GenerateDailyReportSummaryAsync(
        DailyReportMetrics metrics,
        DateTime reportDate,
        CancellationToken ct = default);
}
