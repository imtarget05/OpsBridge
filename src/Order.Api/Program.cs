using FlashSale.Application.Auth;
using FlashSale.Application.Automation;
using FlashSale.Application.Events;
using FlashSale.Application.Inventory;
using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Application.Payment;
using FlashSale.Application.Persistence;
using FlashSale.Application.Saga;
using FlashSale.Application.Outbox;
using FlashSale.Domain;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Entities;
using FlashSale.Domain.Events;
using FlashSale.Domain.Outbox;
using FlashSale.Domain.Saga;
using FlashSale.Infrastructure.Auth;
using FlashSale.Infrastructure.Events;
using FlashSale.Infrastructure.Messaging;
using FlashSale.Infrastructure.Payment;
using FlashSale.Infrastructure.Persistence;
using FlashSale.Infrastructure.Redis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Order.Api.Auth;
using Order.Api.OpenApi;
using Order.Api;
using Order.Api.Extensions;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole();

// ---------------------------------------------------------------
// Composition root: PostgreSQL stays the source of truth; Redis is a
// fast pre-filter; the queue decouples acceptance from fulfillment.
// ---------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=FlashSaleDb;Username=postgres;Password=postgres;Maximum Pool Size=80";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IOrderReadModel, OrderReadModel>();
builder.Services.AddScoped<IDatabaseHealthCheck, DatabaseHealthCheck>();

// Business automation platform (spec §1): one domain-event publisher — RabbitMQ
// topic exchange when Messaging:Provider=RabbitMQ, in-memory otherwise.
builder.Services.AddDomainEventPublisher(builder.Configuration);

// Payment automation (spec §4/§5): config-bound options (never hard-coded),
// guarded payment transitions, timeout scan + manual triggers.
var paymentOptions = builder.Configuration
    .GetSection(PaymentAutomationOptions.SectionName).Get<PaymentAutomationOptions>()
    ?? new PaymentAutomationOptions();
builder.Services.AddSingleton(paymentOptions);
builder.Services.AddScoped<IAutomationRunRepository, AutomationRunRepository>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<PaymentAutomationUseCase>();
builder.Services.AddScoped<RecordPaymentUseCase>();

// Accepted reservation -> order-row SLA: scoped to FlashSale's authoritative
// order/outbox store (ADR-017). The scan does not inspect payment status.
var stuckOrderOptions = builder.Configuration
    .GetSection(StuckOrderOptions.SectionName).Get<StuckOrderOptions>()
    ?? new StuckOrderOptions();
builder.Services.AddSingleton(stuckOrderOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IStuckOrderRepository, StuckOrderRepository>();
builder.Services.AddScoped<StuckOrderScanUseCase>();

// Checkout Saga Orchestration (Phases 9 & 11)
builder.Services.AddScoped<ICheckoutSagaRepository, CheckoutSagaRepository>();
builder.Services.AddHttpClient<IPaymentClient, HttpPaymentClient>();
builder.Services.AddScoped<CheckoutSagaCoordinator>();

// Transactional Outbox & Deduplication Inbox (Phase 10)
builder.Services.AddTransactionalOutbox();

// Inventory automation (spec §6): low-stock alerts, threshold config-bound.
var inventoryOptions = builder.Configuration
    .GetSection(InventoryAutomationOptions.SectionName).Get<InventoryAutomationOptions>()
    ?? new InventoryAutomationOptions();
builder.Services.AddSingleton(inventoryOptions);
builder.Services.AddScoped<IStockAlertRepository, StockAlertRepository>();
builder.Services.AddScoped<LowStockAlertUseCase>();

// Reporting automation (spec §7): daily report aggregation + persistence.
var reportingOptions = builder.Configuration
    .GetSection(ReportingAutomationOptions.SectionName).Get<ReportingAutomationOptions>()
    ?? new ReportingAutomationOptions();
builder.Services.AddSingleton(reportingOptions);
builder.Services.AddScoped<IDailyReportRepository, DailyReportRepository>();
builder.Services.AddScoped<DailyReportUseCase>();

// AI Automation capabilities: inventory reorder recommendations & executive daily report summary
builder.Services.AddScoped<IAiAutomationService, FlashSale.Infrastructure.Services.AiAutomationService>();
builder.Services.AddScoped<GenerateReorderRecommendationUseCase>();
builder.Services.AddScoped<GenerateDailyReportSummaryUseCase>();


// ---------------------------------------------------------------
// Observability (Phase III).
//
// ApiMetrics owns real System.Diagnostics.Metrics instruments; the singleton is
// what starts the MeterListener that aggregates them for the JSON snapshot.
//
// AddOpenApi() costs no new package: Microsoft.AspNetCore.OpenApi already ships
// in the ASP.NET shared framework and is referenced by this project.
// ---------------------------------------------------------------
var apiMetrics = new ApiMetrics();
builder.Services.AddSingleton(apiMetrics);
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
// Swagger UI hosting only (interview smoke gate: GET /swagger → 200). The
// document itself still comes from AddOpenApi above — Swashbuckle's generator
// is never asked to produce one, so /openapi/v1.json stays the single contract.
builder.Services.AddSwaggerGen();

// LOCAL v2 distributed tracing. The exporter is opt-in so the migration Job and
// production manifests remain independent of an observability collector.
builder.Services.AddFlashSaleObservability(builder.Configuration, apiMetrics);

// ---------------------------------------------------------------
// Migration entrypoint (Phase 7C): `dotnet Order.Api.dll --migrate`.
//
// Positioned HERE, before Redis and the queue are registered, because a
// migration must depend on PostgreSQL and nothing else. Two reasons this is not
// cosmetic:
//   * ConnectionMultiplexer.Connect() below is eager, so a Redis-less Job would
//     hang/throw before reaching the migration.
//   * AddOrderQueue() is eager too and fails fast on missing broker config, so
//     placing the branch after it would make the migration Job unrunnable in
//     exactly the environment it exists for.
// infrastructure/kubernetes/base/migration/job.yaml is the only intended caller;
// the API and worker keep calling InitializeAsync for single-process local runs.
// ---------------------------------------------------------------
if (args.Contains("--migrate"))
{
    // ASP0000 is intentional: the migration path must run before the app (and
    // therefore before the queue/Redis singletons) exists, so a throwaway
    // provider is the only way to obtain a logger.
#pragma warning disable ASP0000
    await using var migrationProvider = builder.Services.BuildServiceProvider();
#pragma warning restore ASP0000
    var migrationLogger = migrationProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Migration");

    var exitCode = await MigrationRunner.RunAsync(migrationProvider, migrationLogger);
    migrationLogger.LogInformation("Migration entrypoint finished with exit code {ExitCode}", exitCode);
    return exitCode;
}

// ---------------------------------------------------------------
// Authentication & authorization (ADR-013).
//
// Registered AFTER the --migrate branch, and that ordering is load-bearing:
// JwtOptions.FromConfiguration() fails fast when a signing key is missing, and
// the whole point of the migration Job is to depend on PostgreSQL and nothing
// else. Above the branch, a key-less production Job would refuse to start.
// ---------------------------------------------------------------
builder.Services.AddFlashSaleAuthentication(builder.Configuration, builder.Environment.IsProduction());

// Redis + Queue (ADR-005 / ADR-025): Redis reservation mirror, queue provider,
// order processor, and hosted service — all wired from configuration.
builder.Services.AddFlashSaleRedisAndQueue(builder.Configuration);

// Authoritative Server-side Rate Limiting (ADR-025 / Phase 1)
// Cloudflare edge rate limiting is approximate/per-colocation; the host API must
// enforce strict rate limits to guarantee origin capacity protection.
var rateLimitingEnabled = builder.Configuration.GetValue("RateLimiting:Enabled", true);
builder.Services.AddFlashSaleRateLimiting(builder.Configuration);

var app = builder.Build();

// Start the metrics listener NOW, not on the first scrape. A MeterListener only
// sees the measurements recorded while it is running, and the outbox
// dispatcher starts publishing from the first second of the process: a
// listener created on the first /internal/metrics request would silently lose
// every dispatch that happened before it.
app.Services.GetRequiredService<ApiMetrics>();

// Outermost user middleware: it must see every request — including ones
// rejected by authentication — so the in-flight gauge and the duration
// histogram describe the whole surface, not just the authorized part.
app.UseFlashSaleMetrics();

// Order matters: authentication must populate HttpContext.User before
// rate limiter partitions by authenticated user or authorization evaluates policies.
app.UseAuthentication();
if (rateLimitingEnabled)
{
    app.UseRateLimiter();
}
app.UseAuthorization();

// All HTTP endpoints + startup concerns (DatabaseInitializer) are composed in
// RoutingExtensions.MapFlashSaleEndpoints.
await app.MapFlashSaleEndpoints();

// Explicit, because the --migrate branch above returns an int: top-level
// statements require every code path to return a value once one of them does.
return 0;

public record OrderRequest(int ProductId, int Quantity);
public record SagaCheckoutApiRequest(int ProductId, int Quantity, decimal Amount, string? IdempotencyKey = null);
public record OutboxEnqueueApiRequest(Guid? MessageId, string? EventType, string? Topic, string? Payload);
public record AgentRequeueRequest(
    string IntentId,
    string IntentHash,
    string IdempotencyKey,
    string ApprovedBy,
    DateTimeOffset ExpectedDeadLetteredAt);
public record InboxConsumeApiRequest(Guid MessageId, string ConsumerName);

/// <summary>Body for <c>POST /api/orders/{key}/pay</c> (spec §4 simulation).</summary>
public record PaymentRequest(string? Outcome);

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this exact
/// composition root from the integration test project (Phase IV E2E).
/// </summary>
/// <remarks>
/// The compiler already generates <c>Program</c> as a partial class for top-level
/// statements; this declaration only widens its accessibility. It must stay after
/// the type declarations above, because top-level statements must precede them.
/// </remarks>
public partial class Program
{
    /// <summary>
    /// Read the caller's user id from the JWT <c>sub</c> claim, or <c>null</c> when
    /// the request is anonymous (ADR-013 §6). Exposed as a static member so the
    /// endpoints defined in <see cref="RoutingExtensions"/> can call it.
    /// </summary>
    internal static Guid? TryGetUserId(ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    /// <summary>
    /// Writes one Prometheus exposition line (counter/gauge/summary) for a metric
    /// series key, sanitizing the name and re-quoting tag values. Exposed as a
    /// static member so <see cref="RoutingExtensions"/> can render /metrics.
    /// </summary>
    internal static void AppendSeries(System.Text.StringBuilder sb, string key, string type, string value)
    {
        var name = key;
        var labels = "";
        var brace = key.IndexOf('{');
        if (brace >= 0)
        {
            labels = key[brace..].Replace("\"", "");
            name = key[..brace];
        }
        var metric = SanitizeMetricName(name);
        if (string.IsNullOrEmpty(labels))
        {
            sb.AppendLine($"# TYPE {metric} {type}");
            sb.AppendLine($"{metric} {value}");
        }
        else
        {
            // Re-quote each tag value: {method=GET} -> {method="GET"}
            var inner = labels.Trim('{', '}');
            var quoted = string.Join(",", inner.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t =>
                {
                    var eq = t.IndexOf('=');
                    return eq < 0 ? t : $"{t[..eq]}=\"{t[(eq + 1)..]}\"";
                }));
            sb.AppendLine($"{metric}{{{quoted}}} {value}");
        }
    }

    /// <summary>
    /// Sanitizes a metric series name so it is a valid Prometheus identifier.
    /// Exposed as a static member so <see cref="RoutingExtensions"/> can render /metrics.
    /// </summary>
    internal static string SanitizeMetricName(string raw)
    {
        var chars = raw.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var name = new string(chars);
        return char.IsLetter(name[0]) ? name : "_" + name;
    }
}
