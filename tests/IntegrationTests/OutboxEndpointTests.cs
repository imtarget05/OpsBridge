extern alias order;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlashSale.Application.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

using Program = order.Program;

namespace FlashSale.IntegrationTests;

/// <summary>
/// The operator-facing outbox surface, exercised through the real HTTP
/// pipeline (ADR-019): POST /api/outbox/enqueue must answer 409 on a
/// duplicate MessageId instead of leaking the database's unique violation
/// (23505) as a 500.
/// </summary>
[Collection("flashsale")]
public sealed class OutboxEndpointTests : IAsyncLifetime
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

    public OutboxEndpointTests(FlashSaleFixture fx) => _fx = fx;

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
    public async Task Enqueue_WithADuplicateMessageId_Answers409_Not500()
    {
        using var client = Authorized(await StaffTokenAsync());
        var messageId = Guid.NewGuid();
        var body = new { messageId, eventType = "order.created" };

        var first = await client.PostAsJsonAsync("/api/outbox/enqueue", body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // The SECOND enqueue collides with the unique index
        // (IX_OutboxMessages_MessageId). That is a CONFLICT, not a server
        // fault: before the fix the raw 23505 escaped and the caller got a 500.
        var second = await client.PostAsJsonAsync("/api/outbox/enqueue", body);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var payload = await second.Content.ReadFromJsonAsync<DuplicateConflict>();
        Assert.NotNull(payload);
        Assert.Equal("duplicate_outbox_message_id", payload!.Error);
        Assert.Equal(messageId, payload.MessageId);

        // And the conflict did not create a second row.
        await using var db = _fx.CreateDbContext();
        var rows = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .CountAsync(db.OutboxMessages.Where(m => m.MessageId == messageId));
        Assert.Equal(1, rows);
    }

    private sealed record DuplicateConflict(string Error, Guid MessageId);
}
