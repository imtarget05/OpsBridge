using FlashSale.Application.Automation;
using FlashSale.Application.Events;
using FlashSale.Application.Inventory;
using FlashSale.Application.Messaging;
using FlashSale.Application.Orders;
using FlashSale.Application.Persistence;
using FlashSale.Infrastructure.Automation;
using FlashSale.Infrastructure.Events;
using FlashSale.Infrastructure.Messaging;
using FlashSale.Infrastructure.Persistence;
using FlashSale.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddJsonConsole();
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "order-worker",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithMetrics(metrics =>
    {
        metrics.AddRuntimeInstrumentation();
        // ADR-019: the worker's own instruments are only exported when their
        // meter is REGISTERED here. An OpenTelemetry MeterProvider only
        // observes the meters it was told about, so without this line the
        // pipeline was healthy and exported nothing: every outbox counter
        // (flashsale.worker.outbox.dispatched / .pending / .stuck /
        // .publish_failures / .retried / .dead_lettered) was declared in
        // WorkerTelemetry but never reachable by any exporter.
        metrics.AddMeter(WorkerTelemetry.MeterName);
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
    })
    .WithTracing(tracing =>
    {
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
    });

// Clean Architecture: the worker is another composition root over the same
// Application ports — the use case (OrderProcessor) is shared with the API.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=FlashSaleDb;Username=postgres;Password=postgres;Maximum Pool Size=40";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<OrderProcessor>();

// Query-side port + Redis reservation tier: the automation use cases below
// (PaymentAutomationUseCase et al.) depend on BOTH, exactly like the API
// composition root — without them the payment-timeout scan cannot resolve
// and every iteration dies on GetRequiredService<IOrderReadModel>().
builder.Services.AddScoped<IOrderReadModel, OrderReadModel>();
var redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(redisConnection));
builder.Services.AddSingleton<IStockReservationGateway>(sp =>
    new RedisStockGateway(sp.GetRequiredService<IConnectionMultiplexer>()));

// Queue selection mirrors the API: exactly one provider (ADR-005), and the same
// Production guard. Both composition roots must resolve the SAME provider, or the
// API publishes to a broker nobody consumes from.
builder.Services.AddOrderQueue(builder.Configuration, builder.Environment.EnvironmentName);

builder.Services.AddHostedService<OrderProcessorHost>();

// Business automation platform (spec §1/§11): the same event publisher plus the
// audit-first AutomationWorkerHost (consume automation.events → AutomationRun
// record → workflow logic). Same ADR-005 gate as the order queue: RabbitMQ only,
// so API and worker stay on one topology; both composition roots must agree.
builder.Services.AddDomainEventPublisher(builder.Configuration);
builder.Services.AddAutomationWorkerHost(builder.Configuration);

// Transactional Outbox & Deduplication Inbox (Phase 10/V2.2): the shared
// AutomationEventProcessor resolves IInboxRepository on every event. Register the
// same persistence services in this composition root as in the API; otherwise
// the Kafka consumer exits and the Deployment enters CrashLoopBackOff.
builder.Services.AddTransactionalOutbox();

// Payment automation (spec §4/§5): config-bound options, payment transitions,
// and the periodic abandoned-payment scan. Provider-independent (DB-backed),
// so it runs regardless of the messaging gate above.
var paymentOptions = builder.Configuration
    .GetSection(PaymentAutomationOptions.SectionName).Get<PaymentAutomationOptions>()
    ?? new PaymentAutomationOptions();
builder.Services.AddSingleton(paymentOptions);
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<PaymentAutomationUseCase>();
builder.Services.AddHostedService<PaymentTimeoutHostedService>();

var stuckOrderOptions = builder.Configuration
    .GetSection(StuckOrderOptions.SectionName).Get<StuckOrderOptions>()
    ?? new StuckOrderOptions();
builder.Services.AddSingleton(stuckOrderOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IStuckOrderRepository, StuckOrderRepository>();
builder.Services.AddScoped<StuckOrderScanUseCase>();
builder.Services.AddHostedService<StuckOrderScanHostedService>();

// Inventory automation (spec §6): periodic low-stock scan, same use case as the
// API's manual trigger (trigger_type distinguishes timer vs manual in audit).
var inventoryOptions = builder.Configuration
    .GetSection(InventoryAutomationOptions.SectionName).Get<InventoryAutomationOptions>()
    ?? new InventoryAutomationOptions();
builder.Services.AddSingleton(inventoryOptions);
builder.Services.AddScoped<IStockAlertRepository, StockAlertRepository>();
builder.Services.AddScoped<LowStockAlertUseCase>();
builder.Services.AddHostedService<LowStockScanHostedService>();

// Reporting automation (spec §7): daily scheduler + report persistence.
var reportingOptions = builder.Configuration
    .GetSection(ReportingAutomationOptions.SectionName).Get<ReportingAutomationOptions>()
    ?? new ReportingAutomationOptions();
builder.Services.AddSingleton(reportingOptions);
builder.Services.AddScoped<IDailyReportRepository, DailyReportRepository>();
builder.Services.AddScoped<DailyReportUseCase>();
builder.Services.AddHostedService<DailyReportHostedService>();

var host = builder.Build();
host.Run();

