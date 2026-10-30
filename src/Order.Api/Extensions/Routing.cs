using FlashSale.Application.Auth;
using FlashSale.Application.Automation;
using FlashSale.Application.Inventory;
using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Application.Outbox;
using FlashSale.Application.AgentOps;
using FlashSale.Application.Payment;
using FlashSale.Application.Persistence;
using FlashSale.Application.Saga;
using FlashSale.Domain;
using FlashSale.Domain.Automation;
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
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Order.Api.Auth;
using Order.Api.OpenApi;
using Order.Api;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Order.Api.Extensions;

public static class RoutingExtensions
{
    public static async Task MapFlashSaleEndpoints(this WebApplication app)
    {
        // Startup concerns live in Infrastructure (schema + seed + Redis mirroring).
        await DatabaseInitializer.InitializeAsync(app.Services);

        app.MapAuthEndpoints();

        // OpenAPI document (Phase III). Served as JSON at /openapi/v1.json, which is a
        // stable, tool-agnostic contract: any client that can read a URL can generate a
        // typed client or render docs from it.
        app.MapOpenApi();

        // Interactive UI over the SAME document (GET /swagger -> 200). The endpoint
        // points the UI at /openapi/v1.json, whose Bearer security scheme (added by
        // BearerSecuritySchemeTransformer) is what makes the Authorize button work -
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
        // counters that /internal/metrics exposes to staff - an anonymous reader must not
        // learn login/registration activity. Only business counters + latency histograms.
        app.MapGet("/metrics", (ApiMetrics metrics) =>
        {
            var snap = metrics.Snapshot();
            var sb = new System.Text.StringBuilder();
            foreach (var (key, value) in snap.Counters)
            {
                if (key.Contains(".auth.", StringComparison.OrdinalIgnoreCase)) continue;
                Program.AppendSeries(sb, key, "counter", value.ToString());
            }
            foreach (var (key, value) in snap.Gauges)
            {
                // Outbox lag (pending) and the stuck set are LEVELS, not totals - they
                // are exported as Prometheus gauges so a scraper reads them as "how many
                // rows right now" instead of "how many observations so far".
                Program.AppendSeries(sb, key, "gauge", value.ToString());
            }
            foreach (var (key, hist) in snap.Histograms)
            {
                Program.AppendSeries(sb, key + ".count", "summary", hist.Count.ToString());
                Program.AppendSeries(sb, key + ".sum", "summary", hist.Sum.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }
            return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
        })
        .WithTags("Ops")
        .WithName("PrometheusMetrics")
        .WithSummary("Prometheus text metrics (no auth counters, no auth required).");

        // Automation dashboard (spec §14). DB-only roll-up of today's audit rows, so
        // this endpoint stays read-only and dependency-free: no Redis, no queue, no
        // background timing - just what AutomationRuns already recorded.
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

        app.MapGet("/internal/automation/stuck-orders", async (
            StuckOrderScanUseCase scan, CancellationToken ct) =>
            Results.Ok(await scan.GetCurrentAsync(ct)))
            .RequireAuthorization(new AuthorizeAttribute { Roles = AuthRoles.Admin })
            .WithTags("Ops")
            .WithName("StuckOrders")
            .WithSummary("Accepted reservations older than the SLA with no persisted order or compensation.");

        app.MapPost("/internal/automation/stuck-order-scan", async (
            StuckOrderScanUseCase scan, CancellationToken ct) =>
            Results.Ok(await scan.ExecuteAsync(ct)))
            .RequireAuthorization(new AuthorizeAttribute { Roles = AuthRoles.Admin })
            .WithTags("Ops")
            .WithName("StuckOrderScan")
            .WithSummary("Raise one durable alert event per overdue accepted reservation.");


        // ---------------------------------------------------------------
        // Thin HTTP endpoints: map routes and delegate to Application ports/use cases.
        // No business logic here - only HTTP-to-domain translation.
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
            // is deliberately NOT [Authorize] - the anonymous path is existing,
            // verified behaviour and the concurrency evidence depends on it.
            var userId = Program.TryGetUserId(user);

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
                    // refusal because drift means the two tiers disagreed - worth an alert,
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
                    // process with its own meter instance - counting them here would be a guess.
                    ApiMetrics.OrdersCompleted.Add(1);
                    return Results.Ok(new { message = "Order placed successfully", idempotencyKey = result.IdempotencyKey });

                case SubmitOrderOutcome.InvalidQuantity:
                    ApiMetrics.OrderRejected("invalid_quantity");
                    return Results.BadRequest(new { error = "Quantity must be positive" });

                default:
                    return Results.StatusCode(500);
            }
        })
        .RequireRateLimiting("order-submission");

        // Status polling: did my (idempotent) order actually persist?
        // NOTE (spec §4): this legacy view answers the FULFILLMENT question from row
        // existence alone; the payment lifecycle lives in Order.Status and is exposed
        // by the automation endpoints below - the processing|completed wording is
        // unchanged, because the v1.0 smoke contract depends on it.
        //
        // Authorization (ADR-013 §5): authenticated, and then OWNERSHIP. The key is
        // client-supplied, so treating it as a capability meant anyone who learned a key
        // could read someone else's order. Staff/Admin keep the runbook view; everyone
        // else sees only orders whose UserId is their own `sub`. An anonymous order
        // (UserId = null) is owned by nobody, so only Staff/Admin can poll it.
        // The not-yet-persisted case still answers "processing" - that response body
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
        // Guarded transitions make duplicate calls idempotent (second -> 409).
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
        .WithSummary("Simulated payment gateway: completed -> Confirmed, failed -> stays PendingPayment (spec §4). Requires the caller's own order (or STAFF/ADMIN).");

        // Manual payment-timeout scan (spec §5/§17-A): same use case as the timer,
        // different trigger_type in the audit record - demo-friendly determinism.
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

        // Manual low-stock scan (spec §6/§17-B): DB-backed stock values only - the AI
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
        // ?date=YYYY-MM-DD recomputes a past day (upsert - same row, refreshed figures).
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

        // AI Inventory Reorder Recommendation
        app.MapPost("/internal/automation/reorder-recommendation/{productId:int}", async (
            int productId,
            int? leadTimeDays,
            GenerateReorderRecommendationUseCase useCase,
            CancellationToken ct) =>
        {
            var result = await useCase.ExecuteAsync(productId, leadTimeDays ?? 3, ct);
            return result is null
                ? Results.NotFound(new { error = $"Product {productId} not found or stock is adequate" })
                : Results.Ok(result);
        })
        .RequireAuthorization(AuthPolicies.StaffOrAdmin)
        .WithTags("Ops")
        .WithName("ReorderRecommendation")
        .WithSummary("Generate deterministic EOQ inventory reorder recommendation with AI rationale.");

        // AI Daily Business Report Executive Summary
        app.MapPost("/internal/automation/daily-report/ai-summary", async (
            string? date,
            GenerateDailyReportSummaryUseCase useCase,
            CancellationToken ct) =>
        {
            DateTime? day = null;
            if (!string.IsNullOrWhiteSpace(date))
            {
                if (!DateTime.TryParse(date, out var parsed))
                    return Results.BadRequest(new { error = "date must be YYYY-MM-DD" });
                day = parsed;
            }

            var summary = await useCase.ExecuteAsync(day, ct);
            return Results.Ok(summary);
        })
        .RequireAuthorization(AuthPolicies.StaffOrAdmin)
        .WithTags("Ops")
        .WithName("DailyReportAiSummary")
        .WithSummary("Generate executive AI summary and operational health status from daily aggregates.");

        // The caller's own order history (ADR-013 §5). Scoped by the JWT `sub`, so a
        // caller can only ever see their own orders - no resource-based handler is
        // needed because the query itself is the authorization boundary.
        app.MapGet("/orders/me", async (ClaimsPrincipal user, IOrderReadModel readModel, CancellationToken ct) =>
        {
            var userId = Program.TryGetUserId(user);
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

            var userId = Program.TryGetUserId(httpContext.User);

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
            // envelope - the dispatcher treats a payload without one as poison.
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
            // consumer's dedup key. Left unhandled, the raw 23505 surfaced as a 500 -
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
        // Rows dead-lettered or waiting out a backoff are reported separately - the
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
        // picks them up on its next tick - no SQL by hand, no redeploy.
        app.MapPost("/api/outbox/requeue", async (
            IOutboxRepository outboxRepo,
            CancellationToken ct) =>
        {
            var revived = await outboxRepo.RequeueStuckAsync(ct);
            return Results.Ok(new { status = "requeued", revived });
        })
        .RequireAuthorization(AuthPolicies.StaffOrAdmin);

        // Agent Hub may recover exactly one dead-lettered event after an approved intent.
        // The HMAC key is dedicated to this command surface and has no source default.
        app.MapGet("/internal/agent/outbox/stuck", async (
            HttpContext http,
            IConfiguration config,
            IOutboxRepository outboxRepo,
            CancellationToken ct) =>
        {
            var key = config["AgentRecovery:HmacKey"];
            if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!await AgentRecoveryAuth.VerifyAsync(http.Request, key, ct))
                return Results.Unauthorized();
            var stuck = await outboxRepo.GetStuckAsync(50, ct);
            return Results.Ok(new
            {
                count = stuck.Count,
                deadLettered = stuck.Count(m => m.DeadLetteredAt is not null),
                backingOff = stuck.Count(m => m.DeadLetteredAt is null),
                messages = stuck.Select(m => new { m.MessageId, m.DeadLetteredAt, m.RetryCount })
            });
        });

        app.MapGet("/internal/agent/outbox/pending", async (
            HttpContext http,
            IConfiguration config,
            IOutboxRepository outboxRepo,
            CancellationToken ct) =>
        {
            var key = config["AgentRecovery:HmacKey"];
            if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!await AgentRecoveryAuth.VerifyAsync(http.Request, key, ct))
                return Results.Unauthorized();
            var pending = await outboxRepo.GetUnprocessedAsync(50, ct);
            return Results.Ok(new { count = pending.Count });
        });

        app.MapGet("/internal/agent/outbox/{messageId:guid}", async (
            Guid messageId,
            HttpContext http,
            IConfiguration config,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var key = config["AgentRecovery:HmacKey"];
            if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!await AgentRecoveryAuth.VerifyAsync(http.Request, key, ct))
                return Results.Unauthorized();
            var row = await db.OutboxMessages.AsNoTracking()
                .Where(x => x.MessageId == messageId)
                .Select(x => new { x.MessageId, x.DeadLetteredAt, x.ProcessedAt, x.RetryCount })
                .SingleOrDefaultAsync(ct);
            return row is null ? Results.NotFound() : Results.Ok(row);
        });

        app.MapPost("/internal/agent/outbox/{messageId:guid}/requeue", async (
            Guid messageId,
            HttpContext http,
            IConfiguration config,
            OutboxRecoveryService recovery,
            CancellationToken ct) =>
        {
            var key = config["AgentRecovery:HmacKey"];
            if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!await AgentRecoveryAuth.VerifyAsync(http.Request, key, ct))
                return Results.Unauthorized();

            AgentRequeueRequest? command;
            try { command = await http.Request.ReadFromJsonAsync<AgentRequeueRequest>(cancellationToken: ct); }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "invalid_json" }); }
            if (command is null || string.IsNullOrWhiteSpace(command.IntentId)
                || command.IntentHash.Length != 64 || !command.IntentHash.All(Uri.IsHexDigit)
                || string.IsNullOrWhiteSpace(command.ApprovedBy)
                || string.IsNullOrWhiteSpace(command.IdempotencyKey)
                || command.IdempotencyKey.Length > 128
                || command.ExpectedDeadLetteredAt == default)
                return Results.BadRequest(new { error = "invalid_agent_command" });

            var result = await recovery.RequeueOneAsync(messageId, command.ExpectedDeadLetteredAt,
                command.IntentId, command.IntentHash, command.IdempotencyKey, command.ApprovedBy, ct);
            return result.Status switch
            {
                "requeued" or "already_applied" => Results.Ok(result),
                "not_found" => Results.NotFound(result),
                _ => Results.Conflict(result),
            };
        });

        // AgentOps: read-only investigation of one dead-lettered outbox event.
        // HMAC-authenticated via AgentRecoveryAuth; feature-flagged via AgentOps:Enabled.
        // NO write actions — no requeue, no payment cancel, no order/inventory mutation (§3.1 §7).
        app.MapGet("/internal/agent/outbox/investigate/{messageId:guid}", async (
            Guid messageId,
            HttpContext http,
            IConfiguration config,
            OutboxInvestigationWorkflow workflow,
            CancellationToken ct) =>
        {
            var key = config["AgentRecovery:HmacKey"];
            if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!await AgentRecoveryAuth.VerifyAsync(http.Request, key, ct))
                return Results.Unauthorized();
            var result = await workflow.InvestigateAsync(messageId, null, ct);
            return Results.Ok(result);
        })
        .WithTags("AgentOps")
        .WithName("InvestigateOutboxMessage")
        .WithSummary("Read-only investigation of one dead-lettered outbox event. Triage → Investigator → Critic. No write actions.");

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
        // consumer would then silently skip a real delivery - an integrity/DoS primitive,
        // not a customer-facing feature. Only the consumer side (or an operator) may call it.
        .RequireAuthorization(AuthPolicies.StaffOrAdmin);

        app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));

        // Back-compat alias for compose probes written before the split.
        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

        // Readiness: can this instance safely receive traffic? Postgres is the source
        // of truth so it gates readiness. Redis/RabbitMQ are RUNTIME OPTIONAL - the
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
    }
}
