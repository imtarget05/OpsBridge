using FlashSale.Infrastructure.Persistence;
using Order.Api;
using FlashSale.Infrastructure.Payment;
using FlashSale.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Order.Api.Extensions;

/// <summary>
/// Observability (Phase III): OpenTelemetry tracing + metrics.
/// The OTLP exporter is opt-in so the migration Job and production manifests
/// remain independent of an observability collector.
/// </summary>
public static class ObservabilityExtensions
{
    public static void AddFlashSaleObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        ApiMetrics apiMetrics)
    {
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: "order-api",
                serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(HttpPaymentClient.ActivitySourceName);
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                // The API meter (ApiMetrics) and the worker meter (WorkerTelemetry,
                // where the outbox dispatcher counts live — ADR-019). A MeterProvider
                // only observes the meters it was told about, so both are registered
                // explicitly; otherwise the pipeline was healthy and exported nothing.
                metrics.AddMeter(ApiMetrics.MeterName);
                metrics.AddMeter(WorkerTelemetry.MeterName);
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
                }
            });
    }
}
