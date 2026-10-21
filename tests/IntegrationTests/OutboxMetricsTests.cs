extern alias order;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlashSale.Application.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

using Program = order.Program;

namespace FlashSale.IntegrationTests;

/// <summary>
/// The worker's outbox instruments must actually reach the scrape (ADR-019).
/// A declared metric that no exporter can see is not a metric: before the fix
/// the worker meter (<c>FlashSale.Worker</c>) was never registered with any
/// MeterProvider, so the six <c>flashsale.worker.outbox.*</c> instruments
/// existed only as C# fields and the scrape showed nothing but
/// <c>flashsale.http.*</c>.
/// </summary>
[Collection("flashsale")]
public sealed class OutboxMetricsTests : IAsyncLifetime
{
    private const string StaffPassword = "ops-only-password-from-config";

    /// <summary>Enables POST /api/auth/bootstrap for this host.</summary>
    private const string BootstrapToken = "integration-test-bootstrap-token";

    /// <summary>Must be >= 32 bytes, or JwtOptions refuses to start.</summary>
    private const string SigningKey = "integration-test-signing-key-at-least-32-bytes";

    private const string StaffEmail = "staff@flashsale.local";

    private readonly FlashSaleFixture _fx;
    private readonly Dictionary<string, string?> _previousEnvironment = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public OutboxMetricsTests(FlashSaleFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        await _fx.ResetDatabaseAsync(stock: 25);

        ApplyEnvironment();
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        foreach (var (key, value) in _previousEnvironment)
            Environment.SetEnvironmentVariable(key, value);
        return Task.CompletedTask;
    }

    private void ApplyEnvironment()
    {
        var values = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings__DefaultConnection"] = _fx.PostgresConnectionString,
            ["ConnectionStrings__Redis"] = _fx.RedisConnectionString,
            ["Messaging__Provider"] = "InMemory",
            ["Auth__Jwt__SigningKey"] = SigningKey,
            ["Bootstrap__StaffPassword"] = StaffPassword,
            ["Bootstrap__AdminToken"] = BootstrapToken,
        };

        foreach (var (key, value) in values)
        {
            _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private HttpClient Authorized(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<string> StaffTokenAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(StaffEmail, StaffPassword));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
    }

    [Fact]
    public async Task Metrics_ExposeTheWorkerOutboxSeries()
    {
        using var client = Authorized(await StaffTokenAsync());

        var snapshot = await client.GetStringAsync("/internal/metrics");

        // The outbox gauges live on the worker meter; before the fix no meter
        // was registered and the scrape only ever showed flashsale.http.*.
        Assert.Contains("flashsale.worker.outbox.pending", snapshot);
        Assert.Contains("flashsale.worker.outbox.stuck", snapshot);

        // The Prometheus scrape carries them too — as GAUGES, because pending
        // and stuck are levels, not totals.
        var prometheus = await _client.GetStringAsync("/metrics");
        Assert.Contains("flashsale_worker_outbox_pending", prometheus);
        Assert.Contains("flashsale_worker_outbox_stuck", prometheus);
    }

    [Fact]
    public async Task Dispatcher_IncrementsTheDispatchedCounter()
    {
        using var client = Authorized(await StaffTokenAsync());

        // A row the dispatcher can pick up: it polls every ~2 s.
        var enqueue = await client.PostAsJsonAsync(
            "/api/outbox/enqueue", new { messageId = Guid.NewGuid(), eventType = "order.created" });
        Assert.Equal(HttpStatusCode.OK, enqueue.StatusCode);

        var dispatched = await WaitForDispatchedCounterAsync(client);
        Assert.True(dispatched > 0, "flashsale.worker.outbox.dispatched never left zero");
    }

    /// <summary>
    /// Poll the snapshot until the dispatcher has published something. The
    /// dispatcher polls every ~2 s, so the wait is bounded but generous.
    /// </summary>
    private static async Task<long> WaitForDispatchedCounterAsync(HttpClient client)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var snapshot = await client.GetStringAsync("/internal/metrics");
            using var document = System.Text.Json.JsonDocument.Parse(snapshot);
            if (document.RootElement.TryGetProperty("counters", out var counters)
                && counters.TryGetProperty("flashsale.worker.outbox.dispatched", out var dispatched))
            {
                var value = dispatched.GetInt64();
                if (value > 0) return value;
            }

            await Task.Delay(500);
        }

        return 0;
    }
}
