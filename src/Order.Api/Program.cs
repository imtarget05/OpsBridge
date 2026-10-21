using FlashSale.Application.Auth;
using FlashSale.Application.Automation;
using FlashSale.Application.Events;
using FlashSale.Application.Payment;
using FlashSale.Application.Saga;
using FlashSale.Application.Inventory;
using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Application.Persistence;
using FlashSale.Domain;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Entities;
using FlashSale.Domain.Events;
using FlashSale.Domain.Outbox;
using FlashSale.Domain.Saga;
using FlashSale.Application.Outbox;
using FlashSale.Infrastructure.Auth;
using FlashSale.Infrastructure.Events;
using FlashSale.Infrastructure.Messaging;
using FlashSale.Infrastructure.Payment;
using FlashSale.Infrastructure.Persistence;
using FlashSale.Infrastructure.Redis;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Order.Api;
using Order.Api.Auth;
using Order.Api.OpenApi;
using StackExchange.Redis;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

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


// ---------------------------------------------------------------
// Observability (Phase III).
//
// ApiMetrics owns real System.Diagnostics.Metrics instruments; the singleton is
// what starts the MeterListener that aggregates them for the JSON snapshot.
//
// AddOpenApi() costs no new package: Microsoft.AspNetCore.OpenApi already ships
// in the ASP.NET shared framework and is referenced by this project.
// ---------------------------------------------------------------
builder.Services.AddSingleton<ApiMetrics>();
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
// Swagger UI hosting only (interview smoke gate: GET /swagger → 200). The
// document itself still comes from AddOpenApi above — Swashbuckle's generator
// is never asked to produce one, so /openapi/v1.json stays the single contract.
builder.Services.AddSwaggerGen();

// LOCAL v2 distributed tracing. The exporter is opt-in so the migration Job and
// production manifests remain independent of an observability collector.
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services
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
var jwtOptions = JwtOptions.FromConfiguration(builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddSingleton(jwtOptions);

// Operator bootstrap configuration (ADR-013 §5). Registered as a singleton so
// the auth gate and the startup log below read exactly the same values.
var bootstrapOptions = BootstrapOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(bootstrapOptions);
if (bootstrapOptions.AdminTokenConfigured)
{
    // Not an error, but a standing footgun: while this variable is set, anyone
    // holding it can mint an ADMIN account. Remove it after the first bootstrap.
    Console.Error.WriteLine(
        "WARNING: Bootstrap:AdminToken is configured — POST /api/auth/bootstrap is live. " +
        "Unset it once the initial ADMIN account exists.");
}
else
{
    Console.Error.WriteLine(
        "WARNING: Bootstrap:AdminToken is not configured — POST /api/auth/bootstrap is disabled (404) " +
        "and no ADMIN account can be created. Set it for the first run, then unset it.");
}
builder.Services.AddSingleton<ITokenIssuer>(new JwtTokenIssuer(jwtOptions));
builder.Services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher());
builder.Services.AddScoped<IUserStore, UserStore>();
builder.Services.AddScoped<AuthService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = ClaimTypes.NameIdentifier,
        };
    });

builder.Services.AddAuthorizationBuilder().AddFlashSalePolicies();

// Redis: enabled when reachable, transparently skipped otherwise.
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(redisConnection));
builder.Services.AddSingleton<IStockReservationGateway>(sp =>
    new RedisStockGateway(sp.GetRequiredService<IConnectionMultiplexer>()));

// Queue selection: exactly one provider from configuration (ADR-005).
builder.Services.AddOrderQueue(builder.Configuration, builder.Environment.EnvironmentName);

builder.Services.AddScoped<OrderProcessor>();
builder.Services.AddScoped<SubmitOrderUseCase>();
builder.Services.AddScoped<StockResyncUseCase>();
builder.Services.AddHostedService<OrderProcessorHost>();

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
// authorization evaluates the policies registered above.
app.UseAuthentication();
app.UseAuthorization();

// Startup concerns live in Infrastructure (schema + seed + Redis mirroring).
await DatabaseInitializer.InitializeAsync(app.Services);

app.MapAuthEndpoints();

// OpenAPI document (Phase III). Served as JSON at /openapi/v1.json, which is a
// stable, tool-agnostic contract: any client that can read a URL can generate a
// typed client or render docs from it.
app.MapOpenApi();

// Interactive UI over the SAME document (GET /swagger → 200). The endpoint
// points the UI at /openapi/v1.json, whose Bearer security scheme (added by
// BearerSecuritySchemeTransformer) is what makes the Authorize button work —
// verifying these two gates independently, as the smoke checklist requires.
app.UseSwaggerUI(options =>
    options.SwaggerEndpoint("/openapi/v1.json", "FlashSale API v1"));

// Metrics snapshot (Phase III). Deliberately a JSON snapshot rather than a
// Prometheus exporter: the interview surface needs a dependency-free view of
// what THIS process observed, and the instruments behind it are real Meter
// instruments, so an OTel exporter can be attached later without a rewrite.
// STAFF/ADMIN: the snapshot includes the auth success/failure counters, so an
// anonymous reader learns which logins are failing and how many accounts exist.
app.MapGet("/internal/metrics", (ApiMetrics metrics) => Results.Ok(metrics.Snapshot()))
    .RequireAuthorization(AuthPolicies.StaffOrAdmin)
    .WithTags("Ops")
    .WithName("MetricsSnapshot")
    .WithSummary("In-process metrics snapshot as JSON (counters + duration histograms).");

// Prometheus text exposition for observability/prometheus. UNAUTHENTICATED by
// design (scrapers do not carry cookies), so it deliberately EXCLUDES the auth.*
// counters that /internal/metrics exposes to staff — an anonymous reader must not
// learn login/registration activity. Only business counters + latency histograms.
app.MapGet("/metrics", (ApiMetrics metrics) =>
{
    var snap = metrics.Snapshot();
    var sb = new System.Text.StringBuilder();
    foreach (var (key, value) in snap.Counters)
    {
        if (key.Contains(".auth.", StringComparison.OrdinalIgnoreCase)) continue;
        AppendSeries(sb, key, "counter", value.ToString());
    }
    foreach (var (key, value) in snap.Gauges)
    {
        // Outbox lag (pending) and the stuck set are LEVELS, not totals — they
        // are exported as Prometheus gauges so a scraper reads them as "how many
        // rows right now" instead of "how many observations so far".
        AppendSeries(sb, key, "gauge", value.ToString());
    }
    foreach (var (key, hist) in snap.Histograms)
    {
        AppendSeries(sb, key + ".count", "summary", hist.Count.ToString());
        AppendSeries(sb, key + ".sum", "summary", hist.Sum.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    }
    return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
})
.WithTags("Ops")
.WithName("PrometheusMetrics")
.WithSummary("Prometheus text metrics (no auth counters, no auth required).");

// Series keys arrive as "flashsale.orders.accepted" or
// "flashsale.http.request.duration{method=GET,route=/api/orders}". Dots become
// underscores; the tag block is preserved as real Prometheus labels.
static void AppendSeries(System.Text.StringBuilder sb, string key, string type, string value)
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

static string SanitizeMetricName(string raw)
{
    var chars = raw.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
    var name = new string(chars);
    return char.IsLetter(name[0]) ? name : "_" + name;
}


// Automation dashboard (spec §14). DB-only roll-up of today's audit rows, so
// this endpoint stays read-only and dependency-free: no Redis, no queue, no
// background timing — just what AutomationRuns already recorded.
// ---------------------------------------------------------------
app.MapGet("/internal/automation/summary", async (IAutomationRunRepository runs) =>
{
    var summary = await runs.GetSummaryAsync();
    return Results.Ok(summary);
})
    .RequireAuthorization(AuthPolicies.StaffOrAdmin)
    .WithTags("Ops")
    .WithName("AutomationSummary")
    .WithSummary("Today's automation dashboard roll-up (runs by status, avg duration, top failing workflow).");

app.MapGet("/internal/automation/alerts", async (IStockAlertRepository alerts) =>
{
    var open = await alerts.ListOpenAsync(50);
    return Results.Ok(open);
})
    .RequireAuthorization(AuthPolicies.StaffOrAdmin)
    .WithTags("Ops")
    .WithName("OpenAlerts")
    .WithSummary("Open low-stock alerts (spec §6), newest first.");



// ---------------------------------------------------------------
// Thin HTTP endpoints: map routes and delegate to Application ports/use cases.
// No business logic here — only HTTP-to-domain translation.
// ---------------------------------------------------------------

app.MapPost("/api/orders", async (
    OrderRequest request,
    HttpRequest http,
    ClaimsPrincipal user,
    SubmitOrderUseCase useCase) =>
{
    if (request.Quantity <= 0)
    {
        ApiMetrics.OrderRejected("invalid_quantity");
        return Results.BadRequest(new { error = "Quantity must be positive" });
    }

    var idempotencyKey = http.Headers.TryGetValue("Idempotency-Key", out var headerKey)
        ? headerKey.ToString()
        : Guid.NewGuid().ToString("N");

    // ADR-013 §6: anonymous callers keep working (UserId = null); an
    // authenticated caller's order is attributed to the JWT `sub`. This endpoint
    // is deliberately NOT [Authorize] — the anonymous path is existing,
    // verified behaviour and the concurrency evidence depends on it.
    var userId = TryGetUserId(user);

    // Thin endpoint: all business orchestration lives in SubmitOrderUseCase
    // (Clean Architecture); presentation only maps outcomes to HTTP + metrics.
    var result = await useCase.ExecuteAsync(request.ProductId, request.Quantity, idempotencyKey, userId);

    switch (result.Outcome)
    {
        case SubmitOrderOutcome.Accepted:
            ApiMetrics.OrdersAccepted.Add(1);
            return Results.Accepted(
                $"/api/orders/{result.IdempotencyKey}",
                new { message = "Order accepted", idempotencyKey = result.IdempotencyKey, status = "processing" });

        case SubmitOrderOutcome.DuplicateRequest:
            ApiMetrics.OrderRejected("duplicate");
            return Results.Json(new { error = "Duplicate request", idempotencyKey = result.IdempotencyKey }, statusCode: 409);

        case SubmitOrderOutcome.OutOfStock:
            ApiMetrics.OrderRejected("out_of_stock");
            return Results.Json(new { error = "Out of stock" }, statusCode: 409);

        case SubmitOrderOutcome.StockDrift:
            // ADR-002's guard fired: the conditional UPDATE matched zero rows and the
            // order was refused instead of overselling. Counted separately from a Redis
            // refusal because drift means the two tiers disagreed — worth an alert,
            // whereas a Redis "sold out" is the expected outcome of a flash sale.
            ApiMetrics.StockDrift.Add(1);
            ApiMetrics.OrderRejected("stock_drift");
            return Results.Json(new { error = "Out of stock" }, statusCode: 409);

        case SubmitOrderOutcome.ProductNotFound:
            ApiMetrics.OrderRejected("unknown_product");
            return Results.NotFound(new { error = "Product not found" });

        case SubmitOrderOutcome.SystemBusy:
            ApiMetrics.OrderRejected("backpressure");
            return Results.Json(new { error = "System busy, retry shortly" }, statusCode: 503);

        case SubmitOrderOutcome.CompletedSynchronously:
            ApiMetrics.OrdersAccepted.Add(1);
            // Scope note: this counter means "completed by THIS process". Orders that
            // went through the queue are persisted by Order.Worker, which is a separate
            // process with its own meter instance — counting them here would be a guess.
            ApiMetrics.OrdersCompleted.Add(1);
            return Results.Ok(new { message = "Order placed successfully", idempotencyKey = result.IdempotencyKey });

        case SubmitOrderOutcome.InvalidQuantity:
            ApiMetrics.OrderRejected("invalid_quantity");
            return Results.BadRequest(new { error = "Quantity must be positive" });

        default:
            return Results.StatusCode(500);
    }
});

// Status polling: did my (idempotent) order actually persist?
// NOTE (spec §4): this legacy view answers the FULFILLMENT question from row
// existence alone; the payment lifecycle lives in Order.Status and is exposed
// by the automation endpoints below — the processing|completed wording is
// unchanged, because the v1.0 smoke contract depends on it.
//
// Authorization (ADR-013 §5): authenticated, and then OWNERSHIP. The key is
// client-supplied, so treating it as a capability meant anyone who learned a key
// could read someone else's order. Staff/Admin keep the runbook view; everyone
// else sees only orders whose UserId is their own `sub`. An anonymous order
// (UserId = null) is owned by nobody, so only Staff/Admin can poll it.
// The not-yet-persisted case still answers "processing" — that response body
// carries no order data, so it leaks nothing and keeps the polling client working.
app.MapGet("/api/orders/{idempotencyKey}", async (
    string idempotencyKey,
    ClaimsPrincipal user,
    IOrderReadModel readModel) =>
{
    var order = await readModel.GetOrderStatusAsync(idempotencyKey);
    if (order is null)
        return Results.Json(new { idempotencyKey, status = "processing" });

    var caller = CallerIdentity.From(user);
    if (!caller.IsStaffOrAdmin && (caller.UserId is null || order.UserId != caller.UserId))
        return Results.Forbid();

    return Results.Json(new { order.IdempotencyKey, status = "completed", order.OrderId, order.ProductId, order.Quantity });
})
.RequireAuthorization();

// Simulated payment gateway (spec §4 paid? branch). outcome: completed|failed.
// Guarded transitions make duplicate calls idempotent (second → 409).
app.MapPost("/api/orders/{idempotencyKey}/pay", async (
    string idempotencyKey,
    PaymentRequest request,
    ClaimsPrincipal user,
    RecordPaymentUseCase useCase,
    CancellationToken ct) =>
{
    var outcome = request.Outcome?.ToLowerInvariant() switch
    {
        "completed" or "paid" => PaymentOutcome.Completed,
        "failed" or "fail" => PaymentOutcome.Failed,
        _ => (PaymentOutcome?)null
    };
    if (outcome is null)
        return Results.BadRequest(new { error = "outcome must be 'completed' or 'failed'" });

    // The caller identity is REQUIRED, not optional: this drives the order to
    // Confirmed and publishes revenue-side events, so the use case refuses any
    // caller who does not own the order and is not Staff/Admin.
    var result = await useCase.ExecuteAsync(idempotencyKey, outcome.Value, CallerIdentity.From(user), ct);
    if (!result.Found) return Results.NotFound(new { error = "Order not found" });
    if (result.Forbidden) return Results.Forbid();

    return outcome == PaymentOutcome.Completed
        ? (result.Transitioned
            ? Results.Ok(new { idempotencyKey, status = "confirmed" })
            : Results.Json(new { error = "Order is not pending payment" }, statusCode: 409))
        : (result.Transitioned
            ? Results.Ok(new { idempotencyKey, status = "payment_failed_recorded" })
            : Results.Json(new { error = "Order is not pending payment" }, statusCode: 409));
})
.RequireAuthorization()
.WithTags("Automation")
.WithName("RecordPayment")
.WithSummary("Simulated payment gateway: completed → Confirmed, failed → stays PendingPayment (spec §4). Requires the caller's own order (or STAFF/ADMIN).");

// Manual payment-timeout scan (spec §5/§17-A): same use case as the timer,
// different trigger_type in the audit record — demo-friendly determinism.
app.MapPost("/internal/automation/payment-timeout-scan", async (
    PaymentAutomationUseCase useCase,
    CancellationToken ct) =>
{
    var result = await useCase.ExecuteAsync("manual", ct);
    return Results.Ok(new { trigger = "manual", result.Scanned, result.Reminded, result.Cancelled });
})
.RequireAuthorization(AuthPolicies.StaffOrAdmin)
.WithTags("Ops")
.WithName("PaymentTimeoutScan")
.WithSummary("Run the abandoned-payment scan now: reminders + cancel/stock-release (writes an AutomationRun row).");

// Manual low-stock scan (spec §6/§17-B): DB-backed stock values only — the AI
// layer is never the source of truth for inventory.
app.MapPost("/internal/automation/low-stock-scan", async (
    LowStockAlertUseCase useCase,
    CancellationToken ct) =>
{
    var result = await useCase.ExecuteAsync("manual", ct);
    return Results.Ok(new
    {
        trigger = "manual",
        result.Scanned,
        result.AlertsCreated,
        result.ProductIds
    });
})
.RequireAuthorization(AuthPolicies.StaffOrAdmin)
.WithTags("Ops")
.WithName("LowStockScan")
.WithSummary("Evaluate the low-stock rule for every product and raise deduplicated LOW_STOCK alerts.");

// Daily business report (spec §7/§17-C): aggregates straight from PostgreSQL.
// ?date=YYYY-MM-DD recomputes a past day (upsert — same row, refreshed figures).
app.MapPost("/internal/automation/daily-report", async (
    string? date,
    DailyReportUseCase useCase,
    CancellationToken ct) =>
{
    DateTime? day = null;
    if (!string.IsNullOrWhiteSpace(date))
    {
        if (!DateTime.TryParse(date, out var parsed))
            return Results.BadRequest(new { error = "date must be YYYY-MM-DD" });
        day = parsed;
    }

    var report = await useCase.ExecuteAsync(day, "manual", ct);
    return Results.Ok(report);
})
.RequireAuthorization(AuthPolicies.StaffOrAdmin)
.WithTags("Ops")
.WithName("RunDailyReport")
.WithSummary("Generate (or refresh) the daily business report from database aggregates.");

app.MapGet("/internal/automation/daily-report/latest", async (
    IDailyReportRepository reports,
    CancellationToken ct) =>
{
    var report = await reports.GetLatestAsync(ct);
    return report is null ? Results.NotFound(new { error = "no report yet" }) : Results.Ok(report);
})
.RequireAuthorization(AuthPolicies.StaffOrAdmin)
.WithTags("Ops")
.WithName("LatestDailyReport")
.WithSummary("Most recent persisted daily business report.");

// The caller's own order history (ADR-013 §5). Scoped by the JWT `sub`, so a
// caller can only ever see their own orders — no resource-based handler is
// needed because the query itself is the authorization boundary.
app.MapGet("/orders/me", async (ClaimsPrincipal user, IOrderReadModel readModel, CancellationToken ct) =>
{
    var userId = TryGetUserId(user);
    if (userId is null) return Results.Unauthorized();

    var orders = await readModel.GetOrdersByUserAsync(userId.Value, ct);
    return Results.Ok(new { count = orders.Count, orders });
})
.RequireAuthorization()
.WithTags("Orders")
.WithName("MyOrders")
.WithSummary("List the authenticated caller's orders, newest first.");

// Read endpoint for audits, dashboards and the benchmark harness.
app.MapGet("/api/products/{id}", async (int id, IOrderReadModel readModel) =>
{
    var product = await readModel.GetProductAsync(id);
    return product is null ? Results.NotFound() : Results.Ok(product);
});


// Ops runbook: reconcile the Redis reservation mirror with PostgreSQL truth
// after drift (ADR-003). ?dryRun=true (default) only REPORTS
// {productId, redis, postgres, drift, fixed:false}; ?dryRun=false overwrites
// the Redis counter with the Postgres value and reports fixed:true.
// STAFF/ADMIN: it overwrites the reservation counter for a product, so an
// anonymous caller could zero or inflate live stock during a sale.
app.MapPost("/internal/resync-stock/{productId}", async (
    int productId,
    StockResyncUseCase resync,
    CancellationToken ct,
    bool dryRun = true) =>
{
    var result = await resync.ExecuteAsync(productId, dryRun, ct);
    return result is null
        ? Results.NotFound(new { error = $"Product {productId} not found." })
        : Results.Ok(result);
})
.RequireAuthorization(AuthPolicies.StaffOrAdmin)
.WithTags("Ops")
.WithName("ResyncStock")
.WithSummary("Reconcile the Redis stock mirror with PostgreSQL truth (dryRun=true reports only).");

// ---------------------------------------------------------------
// Checkout Distributed Saga (Phases 9 & 11)
// ---------------------------------------------------------------
app.MapPost("/api/saga/checkout", async (
    SagaCheckoutApiRequest request,
    HttpContext httpContext,
    CheckoutSagaCoordinator coordinator,
    CancellationToken ct) =>
{
    var key = httpContext.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key))
    {
        key = request.IdempotencyKey ?? $"saga-{Guid.NewGuid():N}";
    }

    var userId = TryGetUserId(httpContext.User);

    var command = new CheckoutSagaCommand(
        IdempotencyKey: key,
        ProductId: request.ProductId,
        Quantity: request.Quantity,
        Amount: request.Amount,
        UserId: userId);

    var result = await coordinator.ExecuteSagaAsync(command, ct);

    if (result.Status == SagaStatus.Completed)
    {
        return Results.Ok(result);
    }

    if (result.Status == SagaStatus.Compensated)
    {
        return Results.Json(result, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    return Results.BadRequest(result);
})
// Authenticated, not STAFF/ADMIN: this is the customer-facing checkout path and
// it already attributes the saga to the caller's `sub`, so there is nothing for
// an anonymous caller to gain here that a registered customer does not have.
.RequireAuthorization();

app.MapGet("/api/saga/{idempotencyKey}", async (
    string idempotencyKey,
    ICheckoutSagaRepository repo,
    CancellationToken ct) =>
{
    var saga = await repo.GetByIdempotencyKeyAsync(idempotencyKey, ct);
    return saga is not null ? Results.Ok(saga) : Results.NotFound(new { error = $"Saga '{idempotencyKey}' not found." });
})
// STAFF/ADMIN: this is the saga MACHINE's state (order id, amounts, correlation
// ids, compensation steps) keyed by a client-supplied string, not a per-caller view.
.RequireAuthorization(AuthPolicies.StaffOrAdmin);

// ---------------------------------------------------------------
// Transactional Outbox & Deduplication Inbox (Phase 10)
// ---------------------------------------------------------------
app.MapPost("/api/outbox/enqueue", async (
    OutboxEnqueueApiRequest request,
    IOutboxRepository outboxRepo,
    CancellationToken ct) =>
{
    // ADR-019: a row is only publishable when it carries the canonical
    // envelope — the dispatcher treats a payload without one as poison.
    var eventId = request.MessageId ?? Guid.NewGuid();
    var envelope = new EventEnvelope
    {
        EventId = eventId.ToString("D"),
        EventType = request.EventType ?? "OrderPlacedEvent",
        AggregateType = "manual",
        AggregateId = eventId.ToString("D"),
        OccurredAt = DateTimeOffset.UtcNow,
        CorrelationId = eventId,
        SchemaVersion = EventEnvelope.CurrentSchemaVersion,
        Payload = request.Payload ?? $"{{\"orderId\":\"{Guid.NewGuid()}\",\"amount\":99.99}}"
    };

    var msg = new OutboxMessage
    {
        MessageId = eventId,
        EventType = envelope.EventType,
        Topic = "automation.events",
        Payload = envelope.ToJson(),
        AggregateType = envelope.AggregateType,
        AggregateId = envelope.AggregateId,
        CorrelationId = envelope.CorrelationId,
        SchemaVersion = envelope.SchemaVersion,
        CreatedAt = envelope.OccurredAt
    };

    // A duplicate MessageId is a CONFLICT, not a server fault: the row exists
    // already and the unique index (IX_OutboxMessages_MessageId) protects the
    // consumer's dedup key. Left unhandled, the raw 23505 surfaced as a 500 —
    // telling the caller "the server broke" for a request that simply collided.
    try
    {
        await outboxRepo.EnqueueAsync(msg, ct);
    }
    catch (DuplicateOutboxMessageException ex)
    {
        return Results.Conflict(new
        {
            error = "duplicate_outbox_message_id",
            message = "An outbox message with this MessageId already exists.",
            messageId = ex.MessageId,
            status = "conflict"
        });
    }

    return Results.Ok(new { status = "enqueued", messageId = msg.MessageId, id = msg.Id });
})
// STAFF/ADMIN: this injects arbitrary events into the bus that real consumers
// will act on. It is a producer-side runbook, not a customer operation.
.RequireAuthorization(AuthPolicies.StaffOrAdmin);
// Honest view: `pending` is only what the dispatcher can act on RIGHT NOW.
// Rows dead-lettered or waiting out a backoff are reported separately — the
// previous version returned count=0 while rows sat permanently stuck.
app.MapGet("/api/outbox/pending", async (
    IOutboxRepository outboxRepo,
    CancellationToken ct) =>
{
    var pending = await outboxRepo.GetUnprocessedAsync(50, ct);
    var stuckCount = await outboxRepo.CountStuckAsync(ct);
    return Results.Ok(new { count = pending.Count, stuckCount, messages = pending });
})
.RequireAuthorization(AuthPolicies.StaffOrAdmin);

app.MapGet("/api/outbox/stuck", async (
    IOutboxRepository outboxRepo,
    CancellationToken ct) =>
{
    var stuck = await outboxRepo.GetStuckAsync(50, ct);
    return Results.Ok(new
    {
        count = stuck.Count,
        deadLettered = stuck.Count(m => m.DeadLetteredAt is not null),
        backingOff = stuck.Count(m => m.DeadLetteredAt is null),
        messages = stuck
    });
})
.RequireAuthorization(AuthPolicies.StaffOrAdmin);

// Operator recovery path for rows that exhausted their retries (or are stuck in
// a long backoff). Clears retry/backoff/dead-letter state so the dispatcher
// picks them up on its next tick — no SQL by hand, no redeploy.
app.MapPost("/api/outbox/requeue", async (
    IOutboxRepository outboxRepo,
    CancellationToken ct) =>
{
    var revived = await outboxRepo.RequeueStuckAsync(ct);
    return Results.Ok(new { status = "requeued", revived });
})
.RequireAuthorization(AuthPolicies.StaffOrAdmin);

app.MapPost("/api/inbox/consume", async (
    InboxConsumeApiRequest request,
    IInboxRepository inboxRepo,
    CancellationToken ct) =>
{
    var alreadyProcessed = await inboxRepo.HasBeenProcessedAsync(request.MessageId, request.ConsumerName, ct);
    if (alreadyProcessed)
    {
        return Results.Ok(new { status = "deduplicated", messageId = request.MessageId, processed = false });
    }

    await inboxRepo.MarkProcessedAsync(request.MessageId, request.ConsumerName, ct);
    return Results.Ok(new { status = "processed", messageId = request.MessageId, processed = true });
})
// STAFF/ADMIN, deliberately stricter than "any authenticated user": this writes to
// the DEDUPLICATION LEDGER the real consumers read. An unauthenticated caller
// could mark a messageId+consumerName as already processed and the genuine
// consumer would then silently skip a real delivery — an integrity/DoS primitive,
// not a customer-facing feature. Only the consumer side (or an operator) may call it.
.RequireAuthorization(AuthPolicies.StaffOrAdmin);

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));

// Back-compat alias for compose probes written before the split.
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

// Readiness: can this instance safely receive traffic? Postgres is the source
// of truth so it gates readiness. Redis/RabbitMQ are RUNTIME OPTIONAL — the
// API falls back to synchronous Postgres processing when Redis is down
// (Task 2 verified), so their absence must NOT make the API unready.
app.MapGet("/health/ready", async (IDatabaseHealthCheck healthCheck, ILogger<Program> readinessLogger) =>
{
    var canConnect = await healthCheck.CanConnectAsync();
    if (!canConnect)
        readinessLogger.LogWarning("Readiness check failed: PostgreSQL unreachable.");

    return canConnect
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not-ready" }, statusCode: 503);
});

/// <summary>
/// Read the caller's user id from the JWT <c>sub</c> claim, or <c>null</c> when
/// the request is anonymous (ADR-013 §6).
/// </summary>
/// <remarks>
/// A local function, not a helper class: it is used by exactly two endpoints in
/// this file, and both need the same "anonymous is allowed" answer.
/// </remarks>
static Guid? TryGetUserId(ClaimsPrincipal user)
{
    var sub = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
    return Guid.TryParse(sub, out var id) ? id : null;
}

app.Run();

// Explicit, because the --migrate branch above returns an int: top-level
// statements require every code path to return a value once one of them does.
return 0;

public record OrderRequest(int ProductId, int Quantity);
public record SagaCheckoutApiRequest(int ProductId, int Quantity, decimal Amount, string? IdempotencyKey = null);
public record OutboxEnqueueApiRequest(Guid? MessageId, string? EventType, string? Topic, string? Payload);
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
public partial class Program;
