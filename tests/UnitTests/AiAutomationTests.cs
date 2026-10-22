using FlashSale.Application.Automation;
using FlashSale.Application.Persistence;
using FlashSale.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSale.UnitTests;

public class AiAutomationTests
{
    private readonly AiAutomationService _service = new(NullLogger<AiAutomationService>.Instance);

    [Fact]
    public async Task ReorderRecommendation_WhenStockLow_RecommendsCorrectQuantity()
    {
        // Arrange: stock 2, threshold 5, velocity 14/week (2/day), lead time 3 days
        // Lead time demand = 2 * 3 = 6
        // Safety stock = max(5, ceil(6 * 0.5)) = 5
        // Target = 6 + 5 = 11
        // Needed = 11 - 2 = 9
        var request = new ReorderRecommendationRequest(
            ProductId: 101,
            ProductName: "Tra Den Phuc Long 500g",
            CurrentStock: 2,
            ReorderThreshold: 5,
            SalesVelocityLast7Days: 14,
            SupplierLeadTimeDays: 3);

        // Act
        var result = await _service.GenerateReorderRecommendationAsync(request);

        // Assert
        Assert.Equal(101, result.ProductId);
        Assert.Equal(9, result.RecommendedReorderQuantity);
        Assert.Equal(5, result.SafetyStock);
        Assert.Equal("heuristic_eoq", result.Source);
        Assert.True(result.IsSimulated);
        Assert.Contains("Cần nhập tối thiểu 9 sp", result.Rationale);
    }

    [Fact]
    public async Task ReorderRecommendation_WhenStockSufficient_RecommendsZero()
    {
        // Arrange: current stock 20, target = 6 + 5 = 11 -> no reorder needed
        var request = new ReorderRecommendationRequest(
            ProductId: 102,
            ProductName: "Syrup Dao Monin 700ml",
            CurrentStock: 20,
            ReorderThreshold: 5,
            SalesVelocityLast7Days: 14,
            SupplierLeadTimeDays: 3);

        // Act
        var result = await _service.GenerateReorderRecommendationAsync(request);

        // Assert
        Assert.Equal(0, result.RecommendedReorderQuantity);
        Assert.Contains("Chưa cần lập đơn nhập bổ sung", result.Rationale);
    }

    [Fact]
    public async Task DailyReportSummary_ComputesGroundedInsights()
    {
        // Arrange
        var metrics = new DailyReportMetrics(
            TotalOrders: 100,
            ConfirmedOrders: 85,
            CancelledOrders: 10,
            FailedPayments: 5,
            Revenue: 15_000_000m,
            TopProducts: new List<TopProductView>
            {
                new(ProductId: 1, Name: "Hong Tra Sua", Quantity: 50, Revenue: 10_000_000m)
            });

        var reportDate = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var result = await _service.GenerateDailyReportSummaryAsync(metrics, reportDate);

        // Assert
        Assert.Equal(reportDate, result.ReportDate);
        Assert.Equal("Healthy", result.HealthStatus);
        Assert.Equal("heuristic_engine", result.Source);
        Assert.True(result.IsSimulated);
        Assert.Contains("85 đơn thành công (85.0%)", result.ExecutiveSummary);
        Assert.Contains("15,000,000 VNĐ", result.ExecutiveSummary);
        Assert.Contains("Hong Tra Sua", result.KeyInsights);
    }

    [Fact]
    public async Task DailyReportSummary_HighFailureRate_FlagsAttentionRequiredOrCritical()
    {
        // Arrange: 20 failed out of 100 -> 20% fail rate >= 15% -> Critical
        var metrics = new DailyReportMetrics(
            TotalOrders: 100,
            ConfirmedOrders: 50,
            CancelledOrders: 30,
            FailedPayments: 20,
            Revenue: 5_000_000m,
            TopProducts: Array.Empty<TopProductView>());

        // Act
        var result = await _service.GenerateDailyReportSummaryAsync(metrics, DateTime.UtcNow);

        // Assert
        Assert.Equal("Critical", result.HealthStatus);
    }
}
