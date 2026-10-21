using FlashSale.Application.Persistence;
using FlashSale.Domain.Automation;
using Microsoft.Extensions.Logging;

namespace FlashSale.Application.Automation;

/// <summary>
/// Use case: computes sales velocity from PostgreSQL product state, applies supplier lead time,
/// and delegates to IAiAutomationService for deterministic EOQ + AI narrative recommendation.
/// </summary>
public sealed class GenerateReorderRecommendationUseCase(
    IStockAlertRepository stockAlerts,
    IAiAutomationService aiService,
    ILogger<GenerateReorderRecommendationUseCase> logger)
{
    public async Task<ReorderRecommendationResult?> ExecuteAsync(
        int productId,
        int supplierLeadTimeDays = 3,
        CancellationToken ct = default)
    {
        var products = await stockAlerts.GetLowStockProductsAsync(
            defaultThreshold: 5,
            take: 200,
            ct: ct);

        var product = products.FirstOrDefault(p => p.ProductId == productId);
        if (product is null)
        {
            logger.LogWarning("Product {ProductId} not found in low-stock candidate set", productId);
            return null;
        }

        var salesVelocity = Math.Max(1, (product.ReorderThreshold > 0 ? product.ReorderThreshold * 2 : 10) - product.AvailableStock);

        var request = new ReorderRecommendationRequest(
            ProductId: product.ProductId,
            ProductName: product.Name,
            CurrentStock: product.AvailableStock,
            ReorderThreshold: product.ReorderThreshold > 0 ? product.ReorderThreshold : 5,
            SalesVelocityLast7Days: salesVelocity,
            SupplierLeadTimeDays: Math.Max(1, supplierLeadTimeDays));

        logger.LogInformation(
            "Generating reorder recommendation for product {ProductId} ({ProductName}): Stock={Stock}, Velocity={Velocity}",
            product.ProductId, product.Name, product.AvailableStock, salesVelocity);

        return await aiService.GenerateReorderRecommendationAsync(request, ct);
    }
}
