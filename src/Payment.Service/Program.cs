using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Payment.Service;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole();

builder.Services.AddEndpointsApiExplorer();

// Durable payment store (Phase 12): the idempotency ledger must survive restarts
// and replicas. A ConcurrentDictionary loses every record on pod restart, which
// means a replayed gateway call after a crash would mint a SECOND transaction id
// for the same IdempotencyKey — the exact double-charge bug this service exists
// to prevent. PostgreSQL is the same instance the order API uses (per-service
// table, no cross-service reads), reached through the shared secret contract.
var paymentConnectionString =
    builder.Configuration.GetConnectionString("PaymentDb")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:PaymentDb is not configured. Payment.Service refuses to start without a durable store.");

builder.Services.AddDbContext<PaymentDbContext>(options => options.UseNpgsql(paymentConnectionString));

var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "payment-service",
        serviceVersion: typeof(PaymentServiceMarker).Assembly.GetName().Version?.ToString()))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
        }
    })
    .WithMetrics(metrics =>
    {
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
    });

var app = builder.Build();

// Schema comes from EF migrations, applied once at startup. This service runs a
// single replica (base/payment/deployment.yaml), so unlike the 2-replica order
// API there is no concurrent-migration race to defend against with a Job.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await db.Database.MigrateAsync();
}

// Health endpoints follow the order-api convention (Phase 4): liveness is a
// static alias, readiness gates on the real dependency (PostgreSQL). The old
// /health endpoint is kept for the compose probe written before the split.
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "Payment.Service", timestamp = DateTimeOffset.UtcNow }));
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/health/ready", async (PaymentDbContext db, ILogger<Program> readinessLogger) =>
{
    bool canConnect;
    try
    {
        canConnect = await db.Database.CanConnectAsync();
    }
    catch (Exception ex)
    {
        // A broken connection string must not crash the readiness probe with a
        // 500 — it is exactly the "not ready" signal the probe exists to report.
        readinessLogger.LogWarning(ex, "Readiness check failed: payment store unreachable.");
        canConnect = false;
    }

    if (!canConnect)
        readinessLogger.LogWarning("Readiness check failed: payment store unreachable.");

    return canConnect
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/api/payments", async (ProcessPaymentRequest request, PaymentDbContext db, ILoggerFactory loggerFactory) =>
{
    var activity = Activity.Current;
    var logger = loggerFactory.CreateLogger("Payment.Service");
    logger.LogInformation(
        "Payment request received: IdempotencyKey={IdempotencyKey} TraceId={TraceId} SpanId={SpanId}",
        request.IdempotencyKey,
        activity?.TraceId.ToString() ?? "none",
        activity?.SpanId.ToString() ?? "none");
    if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
    {
        return Results.BadRequest(new { error = "IdempotencyKey is required." });
    }

    // Idempotent replay: return the stored outcome if this key was processed
    // before (by this replica or any replica before a restart).
    var existing = await db.Records.AsNoTracking()
        .FirstOrDefaultAsync(r => r.IdempotencyKey == request.IdempotencyKey);
    if (existing is not null)
    {
        logger.LogInformation("Idempotent replay for key {IdempotencyKey}: status {Status}",
            request.IdempotencyKey, existing.Status);
        return Results.Ok(existing.ToDto());
    }

    // Deterministic simulation rule:
    // Cent value .01 -> success
    // Cent value .02 -> declined (insufficient funds)
    // Cent value .03 -> transient gateway timeout
    var cents = (int)Math.Round((request.Amount - Math.Floor(request.Amount)) * 100);

    PaymentRecord record;
    if (cents == 2)
    {
        record = PaymentRecord.From(request,
            Success: false,
            Status: "Declined",
            TransactionId: null,
            FailureReason: "Insufficient funds in customer account",
            IsTransientError: false);
    }
    else if (cents == 3)
    {
        record = PaymentRecord.From(request,
            Success: false,
            Status: "TimedOut",
            TransactionId: null,
            FailureReason: "Upstream payment gateway timeout",
            IsTransientError: true);
    }
    else
    {
        // Default / .01: Successful payment
        record = PaymentRecord.From(request,
            Success: true,
            Status: "Succeeded",
            TransactionId: $"tx_{Guid.NewGuid():N}",
            FailureReason: null,
            IsTransientError: false);
    }

    try
    {
        db.Records.Add(record);
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
    {
        // A concurrent request with the same key won the primary-key race.
        // Its outcome is authoritative — return it instead of minting a second
        // transaction id for the same payment.
        var winner = await db.Records.AsNoTracking()
            .FirstAsync(r => r.IdempotencyKey == request.IdempotencyKey);
        logger.LogInformation("Lost idempotency race for key {IdempotencyKey}: returning winner status {Status}",
            request.IdempotencyKey, winner.Status);
        return Results.Ok(winner.ToDto());
    }

    // First processing of a TimedOut payment keeps the wire contract the client
    // (HttpPaymentClient) was written against: 504 marks the outcome transient.
    // A replay of the same key returns the stored record with 200 — the client
    // reads IsTransientError from the body and retries on the same signal.
    if (record.Status == "TimedOut")
    {
        return Results.Json(record.ToDto(), statusCode: StatusCodes.Status504GatewayTimeout);
    }

    return Results.Ok(record.ToDto());
});

app.MapGet("/api/payments/{idempotencyKey}", async (string idempotencyKey, PaymentDbContext db) =>
{
    var record = await db.Records.AsNoTracking()
        .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey);
    if (record is not null)
    {
        return Results.Ok(record.ToDto());
    }
    return Results.NotFound(new { error = $"Payment for key '{idempotencyKey}' not found." });
});

app.Run();
