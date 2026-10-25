using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpsBridge.Api;
using OpsBridge.Api.Auth;
using OpsBridge.Api.Endpoints;
using OpsBridge.Infrastructure;
using OpsBridge.Infrastructure.Messaging;
using OpsBridge.Infrastructure.Persistence;
using OpsBridge.Infrastructure.Security;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole();

// ------------------------------------------------------------------
// Schema operation entrypoints (ADR-018): `--migrate` and `--baseline-legacy`.
//
// Positioned HERE — before the DI registration and, critically, before the JWT
// validation block below — because a schema deployment step must depend on PostgreSQL
// and nothing else. The JWT block throws when Jwt:SigningKey is missing, too short, or
// (in Production) still the built-in development key. A key-less production migration
// job would therefore refuse to start even though it has everything it needs: a
// database and a migration. Same reasoning as the FlashSale migration Job in
// src/Order.Api/Program.cs — the branch has to come before every other gate.
//
// The logger is created directly instead of through the ASP.NET host, because these
// branches run BEFORE `builder.Build()`: there is no service provider yet, and the
// ASP0000 throwaway-provider pattern used by the FlashSale equivalent is not needed
// when the logger is the only thing the branch requires.
// ------------------------------------------------------------------
var wantsMigrate = args.Contains("--migrate");
var wantsBaselineLegacy = args.Contains("--baseline-legacy");

if (wantsMigrate || wantsBaselineLegacy)
{
    if (wantsMigrate && wantsBaselineLegacy)
    {
        Console.Error.WriteLine("--migrate and --baseline-legacy are mutually exclusive.");
        return OpsBridgeMigrationRunner.ExitCodeFailure;
    }

    using var schemaLoggerFactory = LoggerFactory.Create(logging => logging
        .AddSimpleConsole()
        .SetMinimumLevel(LogLevel.Information));
    var schemaLogger = schemaLoggerFactory.CreateLogger("OpsBridge.Schema");

    // No localhost fallback here: a schema operation that silently targets the
    // developer's local database would report success while the intended target stays
    // untouched. See OpsBridgeConnectionStringResolver.ResolveForMigration.
    string schemaConnectionString;
    try
    {
        schemaConnectionString = OpsBridgeConnectionStringResolver.ResolveForMigration(builder.Configuration);
    }
    catch (InvalidOperationException ex)
    {
        // The resolver throws on purpose; the deployment entrypoint still has to exit
        // with a code from the contract instead of an unhandled-exception abort. A
        // configuration problem is a one-line operator message: the stack trace of an
        // InvalidOperationException says nothing the message does not.
        schemaLogger.LogError("Schema operation refused: {Message}", ex.Message);
        return OpsBridgeMigrationRunner.ExitCodeFailure;
    }

    var commandTimeoutSeconds = builder.Configuration.GetValue(
        "OpsBridge:MigrationCommandTimeoutSeconds",
        OpsBridgeMigrationRunner.DefaultCommandTimeoutSeconds);

    // NOTHING may escape this entrypoint. The deployment contract is an exit code, so
    // an unhandled exception here would abort the process with exit code 134 and a
    // stack trace instead of the contract code the pipeline expects.
    var schemaExitCode = OpsBridgeMigrationRunner.ExitCodeFailure;
    try
    {
        schemaExitCode = wantsMigrate
            ? await RunMigrationAsync(schemaConnectionString, schemaLogger, commandTimeoutSeconds)
            : await RunBaselineLegacyAsync(schemaConnectionString, schemaLogger);
    }
    catch (OperationCanceledException ex)
    {
        schemaLogger.LogError("Schema operation CANCELLED: {Message}", ex.Message);
        schemaExitCode = OpsBridgeMigrationRunner.ExitCodeFailure;
    }
    catch (Exception ex)
    {
        schemaLogger.LogError(ex, "Schema operation FAILED unexpectedly: {Message}", ex.Message);
        schemaExitCode = OpsBridgeMigrationRunner.ExitCodeFailure;
    }

    schemaLogger.LogInformation(
        "Schema entrypoint {Command} finished with exit code {ExitCode}.",
        wantsMigrate ? "--migrate" : "--baseline-legacy",
        schemaExitCode);

    return schemaExitCode;
}

// ------------------------------------------------------------------
// Controlled runtime initialisation (ADR-018).
//
// `OpsBridge:AutoMigrate` is OFF by default, so nothing about the existing behaviour
// changes: the API and the integration fixture (which migrates explicitly) all keep
// working exactly as before.
//
// When it IS switched on:
//   * Development / Test  -> the schema is migrated at startup and the application
//                           FAILS CLOSED: a failed migration must stop the process
//                           rather than let it serve traffic against a wrong schema.
//   * Anything else      -> REFUSED. Production schema changes belong to the
//                           dedicated `--migrate` deployment step, which runs with a
//                           DDL-capable identity; the runtime identity must hold only
//                           DML rights. A runtime identity that can also change the
//                           schema would make every rollout a schema event.
// ------------------------------------------------------------------
var autoMigrateEnabled = builder.Configuration.GetValue("OpsBridge:AutoMigrate", false);
var isLocalEnvironment =
    builder.Environment.IsDevelopment()
    || builder.Environment.IsEnvironment("Test");

if (autoMigrateEnabled && !isLocalEnvironment)
{
    throw new InvalidOperationException(
        "OpsBridge:AutoMigrate is enabled but the environment is not Development or Test. " +
        "Production schema changes must be applied by the dedicated `--migrate` deployment step, " +
        "running with a DDL-capable identity; the runtime identity must hold only DML rights " +
        "(SELECT/INSERT/UPDATE/DELETE) and must never be able to change the schema. " +
        "Disable OpsBridge:AutoMigrate outside Development and Test.");
}

// ------------------------------------------------------------------
// JSON wire contract: an enum travels as its NAME, never as its ordinal.
//
// The reasoning (and the white screen this fixed) is documented on
// OpsBridgeJsonContract; the short version: the web client declares and
// SENDS enums as names, System.Text.Json's default writes and reads them as
// numbers, and the two halves of the product disagreed on the wire.
//
// The change is strictly additive for requests (the converter still reads
// integers) and a documented response-shape change for responses:
// "status": 1 -> "status": "PendingReconciliation".
//
// The converter is applied globally rather than per DTO so that a new enum
// cannot silently ship as a number.
// ------------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(OpsBridgeJsonContract.EnumConverter);
});

// ------------------------------------------------------------------
// THE JWT SIGNING PARAMETERS ARE READ ONCE, INTO A FROZEN SNAPSHOT.
//
// OpsBridgeJwtConfiguration (src/OpsBridge.Infrastructure/Security/) is the
// single READER of Jwt:SigningKey / Jwt:Issuer / Jwt:Audience. This block
// takes ONE snapshot of the three values (OpsBridgeJwtSettings) and hands
// THAT object to both sides:
//
//   * the token ISSUER (DependencyInjection.cs), and
//   * the token VALIDATION path (the AddJwtBearer block below).
//
// Why the snapshot and not two reads of the configuration: the two sides used
// to read it at two different TIMES - the validation path eagerly, while the
// composition root was built, the issuer lazily, on its first resolution
// after the host was built. IConfiguration's JSON provider reloads on change,
// so an appsettings.json plus a key rotation on disk would have made the
// issuer sign with the NEW key while the validator kept the OLD one: the same
// defect this work fixed (every token the API minted was rejected by the API
// itself, IDX10517 -> 401), in a harder-to-see form. One snapshot, handed to
// both sides, makes the divergence impossible.
//
// The Production gates below are UNCHANGED (same conditions, same messages):
// they are the reason the built-in development key can never reach a
// Production deployment, and they now apply to the snapshot both sides use.
var jwtSettings = OpsBridgeJwtConfiguration.Resolve(builder.Configuration);
var configuredSigningKey = jwtSettings.SigningKey;

if (string.IsNullOrWhiteSpace(builder.Configuration["Jwt:SigningKey"]))
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException("Jwt:SigningKey must be configured in Production.");
    }
}

if (builder.Environment.IsProduction() &&
    string.Equals(configuredSigningKey, OpsBridgeJwtTokenIssuer.DefaultSigningKey, StringComparison.Ordinal))
{
    throw new InvalidOperationException("Jwt:SigningKey must not use the built-in development key in Production.");
}

if (Encoding.UTF8.GetByteCount(configuredSigningKey) < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 UTF-8 bytes.");
}

// ------------------------------------------------------------------
// Clean Architecture Composition Root: OpsBridge API
// Infrastructure adapters are registered via AddOpsBridgeInfrastructure
// ------------------------------------------------------------------
// The FROZEN snapshot is passed in, so the issuer is built from the same
// values the validation path below uses - not from a second read of the
// configuration at a later moment.
builder.Services.AddOpsBridgeInfrastructure(builder.Configuration, jwtSettings);
builder.Services.AddHttpClient("factoryflow-ping", client =>
    client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(ConnectorPingNetworkPolicy.CreateHandler);

// CORS for Vite frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("OpsBridgeWebPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Authentication & Authorization
//
// THE SIGNING KEY IS READ IN EXACTLY ONE PLACE (OpsBridgeJwtConfiguration,
// src/OpsBridge.Infrastructure/Security/OpsBridgeJwtConfiguration.cs), and the
// token ISSUER uses the same reader (DependencyInjection.cs). That is the fix
// for the defect this block used to hide: the issuer was registered without
// configuration and always signed with the built-in development key, while
// this block validated with the CONFIGURED key — so in Production, where the
// two must differ, every token the API minted was rejected by the API itself
// (IDX10517 -> 401 on every login).
//
// The Production gates therefore apply to BOTH sides: a Production
// deployment cannot sign with a key it would refuse to validate, and it
// cannot validate with a key it would refuse to sign with. Both sides read
// the SAME frozen snapshot (jwtSettings), so they cannot disagree.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization(options => options.AddOpsBridgePolicies());

var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "opsbridge-api",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics.AddRuntimeInstrumentation();
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            metrics.AddOtlpExporter(options => options.Endpoint = new Uri(otlpEndpoint));
        }
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Authoritative Server-side Rate Limiting (ADR-025 / Phase 1)
var rateLimitingEnabled = builder.Configuration.GetValue("RateLimiting:Enabled", true);
if (rateLimitingEnabled)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, token) =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var path = context.HttpContext.Request.Path;
            logger.LogWarning("Host rate limit exceeded for client {ClientIp} on {Path}", clientIp, path);

            context.HttpContext.Response.ContentType = "application/problem+json";
            var retrySeconds = 60;
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                retrySeconds = (int)Math.Max(1, retryAfter.TotalSeconds);
            }
            context.HttpContext.Response.Headers.RetryAfter = retrySeconds.ToString();

            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc7807",
                title = "Too Many Requests",
                status = StatusCodes.Status429TooManyRequests,
                detail = "Authoritative server-side rate limit exceeded. Edge approximations reinforced by origin policy.",
                retryAfterSeconds = retrySeconds
            }, cancellationToken: token);
        };

        var permitLimit = builder.Configuration.GetValue("RateLimiting:OpsBridgePermitLimit", 60);
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var clientKey = httpContext.User?.Identity?.IsAuthenticated == true
                ? (httpContext.User.Identity.Name ?? "authenticated")
                : (httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous");

            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: clientKey,
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
        });
    });
}

var app = builder.Build();

// Controlled runtime initialisation (ADR-018). Development/Test only — the Production
// and any-other-environment case was already refused above, before the host was built.
// Fail closed: if the schema cannot be brought up to date, the process must not go on
// to serve traffic against a schema it does not know.
if (autoMigrateEnabled)
{
    using var autoMigrateLoggerFactory = LoggerFactory.Create(logging => logging
        .AddSimpleConsole()
        .SetMinimumLevel(LogLevel.Information));
    var autoMigrateLogger = autoMigrateLoggerFactory.CreateLogger("OpsBridge.AutoMigrate");

    // The runtime path resolves the connection string the SAME way the application
    // does (localhost fallback included): the schema that is migrated at startup has to
    // be the schema the application then talks to.
    var autoMigrateConnectionString =
        OpsBridgeConnectionStringResolver.ResolveForApplication(app.Configuration);
    var autoMigrateTimeoutSeconds = app.Configuration.GetValue(
        "OpsBridge:MigrationCommandTimeoutSeconds",
        OpsBridgeMigrationRunner.DefaultCommandTimeoutSeconds);

    // No cancellation token here: the auto-migration runs BEFORE `app.Run()`, so no
    // host lifetime token exists yet — `ApplicationStopping` is never signalled while
    // this code runs, and passing it would suggest a clean interruption that cannot
    // happen. The migration is bounded by the command timeout instead, and the
    // process is terminated by the orchestrator if it has to be.
    var autoMigrateOutcome = await OpsBridgeMigrationRunner.RunAsync(
        autoMigrateConnectionString,
        autoMigrateLogger,
        autoMigrateTimeoutSeconds,
        CancellationToken.None);

    if (autoMigrateOutcome.ExitCode != OpsBridgeMigrationRunner.ExitCodeSuccess)
    {
        throw new InvalidOperationException(
            $"OpsBridge:AutoMigrate is enabled but the migration failed with exit code " +
            $"{autoMigrateOutcome.ExitCode}. Failure: {autoMigrateOutcome.Failure}. " +
            "The application will not start against an unknown schema.");
    }

    autoMigrateLogger.LogInformation(
        "Auto-migration finished. AlreadyAtHead={AlreadyAtHead}, AppliedByThisRun=[{Applied}].",
        autoMigrateOutcome.AlreadyAtHead,
        string.Join(", ", autoMigrateOutcome.AppliedMigrations));
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("OpsBridgeWebPolicy");
app.UseHttpMetrics(options => options.ReduceStatusCodeCardinality());
app.UseAuthentication();
if (rateLimitingEnabled)
{
    app.UseRateLimiter();
}
app.UseAuthorization();

// Liveness & readiness probes
app.MapGet("/healthz", () => Results.Ok(new { status = "Healthy", service = "OpsBridge.Api", timestamp = DateTimeOffset.UtcNow }))
   .WithTags("Health");
app.MapGet("/health/messaging", (MessagingRuntimeStatus status) =>
        status.ConsumerConnected
            ? Results.Ok(new { status = "Connected", component = "RabbitMQ consumer" })
            : Results.Json(new { status = "Degraded", component = "RabbitMQ consumer" }, statusCode: 503))
   .AllowAnonymous()
   .WithTags("Health");

// Map Domain & Ingestion Endpoints
app.MapAuthEndpoints(app.Environment);
app.MapWebhookEndpoints();
app.MapConnectorEndpoints();
app.MapIngestionEndpoints();
app.MapReplayEndpoints();
app.MapReconciliationEndpoints();
app.MapAiEndpoints();

app.MapMetrics("/metrics").AllowAnonymous().WithTags("Metrics");

app.Run();

// Explicit, because the schema entrypoint branches above return an int: once one
// top-level code path returns a value, every path has to.
return 0;

// ------------------------------------------------------------------
// Schema entrypoint helpers.
//
// Deliberately thin: what "success" means, what counts as drift and what the tool is
// allowed to write live in Infrastructure, where they can be exercised without a web
// host. Program.cs only maps configuration and log output onto exit codes:
//   0 = success (or "already at head")
//   1 = failure
//   2 = drift detected (the baseline refused to adopt the database)
// ------------------------------------------------------------------
static async Task<int> RunMigrationAsync(
    string connectionString, ILogger logger, int commandTimeoutSeconds)
{
    var outcome = await OpsBridgeMigrationRunner.RunAsync(connectionString, logger, commandTimeoutSeconds);

    logger.LogInformation(
        "Migration outcome: ExitCode={ExitCode}, AlreadyAtHead={AlreadyAtHead}, Pending=[{Pending}], " +
        "AppliedByThisRun=[{Applied}], NewlyRecorded=[{NewlyRecorded}], Recorded=[{Recorded}], Failure={Failure}",
        outcome.ExitCode,
        outcome.AlreadyAtHead,
        string.Join(", ", outcome.PendingMigrations),
        string.Join(", ", outcome.AppliedMigrations),
        string.Join(", ", outcome.NewlyRecordedMigrations),
        string.Join(", ", outcome.RecordedMigrations),
        outcome.Failure ?? "(none)");

    return outcome.ExitCode;
}

static async Task<int> RunBaselineLegacyAsync(string connectionString, ILogger logger)
{
    var outcome = await OpsBridgeLegacyBaseline.AdoptAsync(connectionString, logger);

    foreach (var difference in outcome.Differences)
    {
        logger.LogError("Baseline difference: {Difference}", difference);
    }

    logger.LogInformation(
        "Baseline outcome: ExitCode={ExitCode}, AlreadyManaged={AlreadyManaged}, DriftDetected={DriftDetected}, " +
        "HistoryWritten={HistoryWritten}, VerificationRan={VerificationRan}, Differences={DifferenceCount}, " +
        "PendingMigrations=[{Pending}], Failure={Failure}",
        outcome.ExitCode,
        outcome.AlreadyManaged,
        outcome.DriftDetected,
        outcome.HistoryWritten,
        outcome.VerificationRan,
        outcome.Differences.Count,
        string.Join(", ", outcome.PendingMigrations),
        outcome.Failure ?? "(none)");

    return outcome.ExitCode;
}

// Required for WebApplicationFactory in integration tests
public partial class Program { }
