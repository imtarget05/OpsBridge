extern alias payment;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;

using ProcessPaymentRequest = payment.Payment.Service.ProcessPaymentRequest;
using PaymentRecordDto = payment.Payment.Service.PaymentRecordDto;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Payment.Service integration tests (Phase 12): drives the REAL Program.cs
/// composition over HTTP against a real PostgreSQL (Testcontainers). Covers the
/// payment lifecycle (success / decline / transient timeout), durable
/// idempotency (replay and restart), and the health endpoints.
/// No real payment provider is contacted — the service IS the deterministic
/// simulator by contract (cents .01/.02/.03).
/// </summary>
/// <remarks>
/// Order.Api and Payment.Service both declare a top-level <c>Program</c> class,
/// which collides when both are referenced from this test assembly. To avoid
/// the CS0433 ambiguity we point <see cref="WebApplicationFactory{TEntryPoint}"/>
/// at <see cref="PaymentServiceMarker"/> (a test-only type that lives in the
/// payment host assembly) so the factory loads the payment host, and we use
/// <see cref="JsonElement"/>/anonymous payloads so no shared DTO types are
/// needed across assemblies.
/// </remarks>
public sealed class PaymentServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .WithDatabase("FlashSaleDb")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new CustomWebApplicationFactory(_postgres.GetConnectionString());
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static HttpContent Request(string key, decimal amount) => new StringContent(
        $$"""{"paymentId":"00000000-0000-0000-0000-000000000000","orderId":1,"idempotencyKey":"{{key}}","amount":{{amount}},"currency":"VND"}""",
        System.Text.Encoding.UTF8,
        "application/json");

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return doc.RootElement;
    }


    [Fact]
    public async Task SuccessfulPayment_ReturnsTransaction_AndReplayReturnsIdenticalRecord()
    {
        var key = $"ok-{Guid.NewGuid():N}";

        var first = await _client.PostAsync("/api/payments", Request(key, 10.01m));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var record = await BodyAsync(first);
        Assert.True(record.GetProperty("success").GetBoolean());
        Assert.Equal("Succeeded", record.GetProperty("status").GetString());
        Assert.StartsWith("tx_", record.GetProperty("transactionId").GetString());
        Assert.False(record.GetProperty("isTransientError").GetBoolean());

        var replay = await _client.PostAsync("/api/payments", Request(key, 10.01m));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await BodyAsync(replay);
        Assert.Equal(
            record.GetProperty("transactionId").GetString(),
            replayed.GetProperty("transactionId").GetString());
        Assert.Equal(
            record.GetProperty("processedAt").GetDateTimeOffset(),
            replayed.GetProperty("processedAt").GetDateTimeOffset());
        Assert.Equal(
            0,
            record.GetProperty("processedAt").GetDateTimeOffset().Ticks % TimeSpan.TicksPerMicrosecond);
    }

    [Fact]
    public async Task NewlyCreatedPayment_UsesPostgresCompatibleMicrosecondTimestamp()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var response = await _client.PostAsync(
                "/api/payments",
                Request($"timestamp-{Guid.NewGuid():N}", 10.01m));
            var record = await BodyAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                0,
                record.GetProperty("processedAt").GetDateTimeOffset().Ticks % TimeSpan.TicksPerMicrosecond);
        }
    }

    [Fact]
    public async Task DeclinedPayment_HasNoTransactionId_AndReplayStaysDeclined()
    {
        var key = $"declined-{Guid.NewGuid():N}";

        var first = await _client.PostAsync("/api/payments", Request(key, 10.02m));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var record = await BodyAsync(first);
        Assert.False(record.GetProperty("success").GetBoolean());
        Assert.Equal("Declined", record.GetProperty("status").GetString());
        Assert.Null(record.GetProperty("transactionId").GetString());
        Assert.False(record.GetProperty("isTransientError").GetBoolean());

        var replay = await _client.PostAsync("/api/payments", Request(key, 10.02m));
        var replayed = await BodyAsync(replay);
        Assert.Equal("Declined", replayed.GetProperty("status").GetString());
        Assert.Null(replayed.GetProperty("transactionId").GetString());
    }

    [Fact]
    public async Task TransientTimeout_Returns504_AndReplayReturnsStoredOutcomeWith200()
    {
        var key = $"timeout-{Guid.NewGuid():N}";

        var first = await _client.PostAsync("/api/payments", Request(key, 10.03m));
        Assert.Equal(HttpStatusCode.GatewayTimeout, first.StatusCode);
        var record = await BodyAsync(first);
        Assert.False(record.GetProperty("success").GetBoolean());
        Assert.Equal("TimedOut", record.GetProperty("status").GetString());
        Assert.True(record.GetProperty("isTransientError").GetBoolean());
        Assert.Null(record.GetProperty("transactionId").GetString());

        // A replay of the same key is an idempotent read of the stored outcome
        // (200), never a second gateway attempt with a new transaction id.
        var replay = await _client.PostAsync("/api/payments", Request(key, 10.03m));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await BodyAsync(replay);
        Assert.Equal("TimedOut", replayed.GetProperty("status").GetString());
        Assert.True(replayed.GetProperty("isTransientError").GetBoolean());
        Assert.Null(replayed.GetProperty("transactionId").GetString());
    }

    [Fact]
    public async Task MissingIdempotencyKey_IsRejected()
    {
        var response = await _client.PostAsync("/api/payments", Request("", 10.01m));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownKey_Returns404()
    {
        var response = await _client.GetAsync($"/api/payments/never-processed-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Replay_AfterProcessRestart_ReturnsStoredOutcome()
    {
        var key = $"restart-{Guid.NewGuid():N}";
        var first = await _client.PostAsync("/api/payments", Request(key, 20.01m));
        var record = await BodyAsync(first);

        // Dispose the host (the process-death equivalent) and boot a new one
        // against the SAME database. The in-memory dictionary of the previous
        // implementation would have lost the record here; the durable ledger
        // must not.
        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = new CustomWebApplicationFactory(_postgres.GetConnectionString());
        _client = _factory.CreateClient();

        var replay = await _client.PostAsync("/api/payments", Request(key, 20.01m));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await BodyAsync(replay);
        Assert.Equal(
            record.GetProperty("transactionId").GetString(),
            replayed.GetProperty("transactionId").GetString());
        Assert.Equal(
            record.GetProperty("processedAt").GetDateTimeOffset(),
            replayed.GetProperty("processedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task HealthEndpoints_LivenessIsStatic_ReadinessGatesOnPostgres()
    {
        var live = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var alias = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, alias.StatusCode);

        var ready = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        // Drop the database out from under the running host from the maintenance
        // database (postgres), since PostgreSQL refuses to drop the currently
        // open database. Liveness stays green (process is fine); readiness flips
        // to 503 (the dependency is gone).
        var adminCs = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = "postgres"
        }.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminCs))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = "DROP DATABASE \"FlashSaleDb\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }

        var readyAfterLoss = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyAfterLoss.StatusCode);

        var liveAfterLoss = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveAfterLoss.StatusCode);
    }
}


