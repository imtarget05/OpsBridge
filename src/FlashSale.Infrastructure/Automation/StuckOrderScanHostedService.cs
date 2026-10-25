using FlashSale.Application.Automation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlashSale.Infrastructure.Automation;

/// <summary>Periodic scan of accepted reservations still lacking an Order.</summary>
public sealed class StuckOrderScanHostedService(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<StuckOrderScanHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue<int?>(
            $"{StuckOrderOptions.SectionName}:ScanIntervalSeconds") ?? 60;
        var interval = TimeSpan.FromSeconds(Math.Max(5, intervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var scan = scope.ServiceProvider.GetRequiredService<StuckOrderScanUseCase>();
                var result = await scan.ExecuteAsync(stoppingToken);
                if (result.AlertsCreated > 0)
                    logger.LogWarning(
                        "Accepted-order stall scan: {AlertsCreated} new alert(s) among {Scanned} overdue reservation(s).",
                        result.AlertsCreated, result.Scanned);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Accepted-order stall scan failed; retrying next interval.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
