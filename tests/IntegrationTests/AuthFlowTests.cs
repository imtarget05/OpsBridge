extern alias order;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlashSale.Application.Auth;
using FlashSale.Domain;

using Program = order.Program;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Phase IV end-to-end auth flow: drives the REAL <c>Program.cs</c> composition
/// over HTTP against the containerised PostgreSQL and Redis, and asserts the
/// journey the interview demo walks
/// (register → login → search → order → Completed → /orders/me).
/// </summary>
/// <remarks>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> is used deliberately instead
/// of re-composing the services by hand: the point is to exercise the actual
/// middleware order, the actual JWT validation parameters and the actual endpoint
/// filters. A hand-built host would pass even if Program.cs were wired wrongly.
/// <para>
/// The queue is pinned to InMemory so the order path is deterministic and
/// in-process — <c>OrderProcessorHost</c> runs inside this host and consumes what
/// the endpoint enqueued, which is also why the flow can reach Completed without
/// a second process.
/// </para>
/// </remarks>
[Collection("flashsale")]
public sealed class AuthFlowTests : IAsyncLifetime
{
    private const string Password = "correct-horse-battery-staple";

    /// <summary>Must be >= 32 bytes, or JwtOptions refuses to start.</summary>
    private const string SigningKey = "integration-test-signing-key-at-least-32-bytes";

    /// <summary>Seed credential for the STAFF account, supplied as configuration.</summary>
    private const string StaffPassword = "ops-only-password-from-config";

    private const string StaffEmail = "staff@flashsale.local";

    private readonly FlashSaleFixture _fx;
    private readonly Dictionary<string, string?> _previousEnvironment = [];
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private int _productId;

    public AuthFlowTests(FlashSaleFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        // Schema from zero, plus one product with known stock.
        _productId = await _fx.ResetDatabaseAsync(stock: 25);

        ApplyEnvironment();
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    /// <summary>
    /// Point the API host at the test containers.
    /// </summary>
    /// <remarks>
    /// Environment variables, NOT <c>WithWebHostBuilder(...).ConfigureAppConfiguration(...)</c>.
    /// That is a real constraint, not a style choice: this app is minimal-hosting
    /// (top-level statements), and WebApplicationFactory replays the
    /// <c>WithWebHostBuilder</c> callbacks during <c>builder.Build()</c> — which is
    /// <em>after</em> Program.cs has already read <c>builder.Configuration</c> for
    /// <c>AddOrderQueue</c> and the connection strings. Only variables present when
    /// <c>WebApplication.CreateBuilder</c> runs are visible to those call sites.
    /// </remarks>
    private void ApplyEnvironment()
    {
        var values = new Dictionary<string, string?>
        {
            // Development, because InMemory is refused in Production by design
            // (ADR-005) — that guard is tested elsewhere, not here.
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings__DefaultConnection"] = _fx.PostgresConnectionString,
            ["ConnectionStrings__Redis"] = _fx.RedisConnectionString,
            // Deterministic, in-process queue: OrderProcessorHost runs inside this
            // host, so an accepted order reaches Completed without a second process.
            ["Messaging__Provider"] = "InMemory",
            ["Auth__Jwt__SigningKey"] = SigningKey,
            // The STAFF account used to be seeded from a password literal in
            // DatabaseInitializer. It is now opt-in via configuration, which is
            // why the ops read below authenticates instead of assuming a seed.
            ["Bootstrap__StaffPassword"] = StaffPassword,
        };

        foreach (var (key, value) in values)
        {
            _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, value) in _previousEnvironment)
            Environment.SetEnvironmentVariable(key, value);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

        // Restore, so these do not leak into the other integration collections.
        RestoreEnvironment();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static string NewEmail() => $"e2e+{Guid.NewGuid():N}@example.com";

    /// <summary>A client carrying the given access token, so headers never leak between tests.</summary>
    private HttpClient Authorized(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<AuthResponse> RegisterAsync(string email)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>
    /// Poll until the worker has persisted the order.
    /// </summary>
    /// <remarks>
    /// The caller must be the order's OWNER. <c>GET /api/orders/{key}</c> is
    /// authenticated and ownership-checked (the key is client-supplied, so it was
    /// never a capability); passing the anonymous client here would now get 401.
    /// </remarks>
    private async Task<string?> WaitForCompletionAsync(HttpClient client, string idempotencyKey)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? status = null;

        while (DateTime.UtcNow < deadline)
        {
            var view = await client.GetFromJsonAsync<OrderStatusResponse>($"/api/orders/{idempotencyKey}");
            status = view?.Status;
            if (status == "completed") return status;
            await Task.Delay(250);
        }

        return status;
    }

    private sealed record OrderStatusResponse(string? Status, int? OrderId, int? ProductId, int? Quantity);

    private sealed record MyOrdersResponse(int Count, List<OrderSummary> Orders);

    private sealed record OrderSummary(int OrderId, int ProductId, int Quantity, DateTime CreatedAt);

    private sealed record ProblemBody(string? Error);

    // ---------------------------------------------------------------
    // The journey the demo walks
    // ---------------------------------------------------------------

    [Fact]
    public async Task FullFlow_Register_Login_Search_Order_Completed_AndMyOrders()
    {
        var email = NewEmail();

        // 1. register -> token pair, CUSTOMER role
        var registered = await RegisterAsync(email);
        Assert.Equal(email, registered.Email);
        Assert.Equal(AuthRoles.Customer, registered.Role);
        Assert.False(string.IsNullOrWhiteSpace(registered.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(registered.RefreshToken));

        // 2. login -> a fresh token pair for the same identity
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var session = (await loginResponse.Content.ReadFromJsonAsync<AuthResponse>())!;

        using var authed = Authorized(session.AccessToken);

        // 3. the bearer token is actually accepted, and carries the caller's identity
        var me = await authed.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(email, await me.Content.ReadAsStringAsync());

        // 4. "search": the catalogue read is reachable and the product exists
        var product = await authed.GetAsync($"/api/products/{_productId}");
        Assert.Equal(HttpStatusCode.OK, product.StatusCode);

        // 5. place the order as the authenticated caller
        var idempotencyKey = $"e2e-{Guid.NewGuid():N}";
        var orderRequest = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { productId = _productId, quantity = 1 }),
        };
        orderRequest.Headers.Add("Idempotency-Key", idempotencyKey);

        var orderResponse = await authed.SendAsync(orderRequest);
        Assert.True(
            orderResponse.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK,
            $"order was not accepted: {(int)orderResponse.StatusCode} {await orderResponse.Content.ReadAsStringAsync()}");

        // 6. 202 Accepted is not "done" — wait for the pipeline to persist it
        Assert.Equal("completed", await WaitForCompletionAsync(authed, idempotencyKey));

        // 7. the caller's own history contains exactly this order
        var mine = await authed.GetFromJsonAsync<MyOrdersResponse>("/orders/me");
        Assert.NotNull(mine);
        Assert.Equal(1, mine!.Count);
        Assert.Equal(_productId, mine.Orders[0].ProductId);
        Assert.Equal(1, mine.Orders[0].Quantity);
    }

    [Fact]
    public async Task Refresh_RotatesTheGeneration_AndRejectsAReplay()
    {
        var session = await RegisterAsync(NewEmail());

        var first = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rotated = (await first.Content.ReadFromJsonAsync<AuthResponse>())!;

        // A rotation that returned the same token would still "succeed" while
        // leaving the replayed token usable — so assert the value actually changed.
        Assert.NotEqual(session.RefreshToken, rotated.RefreshToken);

        var replay = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The rotated token is still good: only the replayed generation was burned.
        var second = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(rotated.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task AccessToken_CannotBePresentedAsARefreshToken()
    {
        var session = await RegisterAsync(NewEmail());

        // Both are HS256 JWTs signed by the same key, so only the `typ` claim
        // keeps one from being usable as the other.
        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.AccessToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_InvalidatesRefresh_ButTheAccessTokenOutlivesIt()
    {
        var session = await RegisterAsync(NewEmail());
        using var authed = Authorized(session.AccessToken);

        var logout = await authed.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var refresh = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // The documented trade-off (ADR-013 §4): no per-request DB lookup, so the
        // access token stays valid for the remainder of its <= 15 minute life.
        // Asserted rather than assumed, so the behaviour cannot drift unnoticed.
        var me = await authed.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task MyOrders_IsScopedToTheCaller()
    {
        var productId = _productId;
        var alice = await RegisterAsync(NewEmail());
        var bob = await RegisterAsync(NewEmail());

        using var aliceClient = Authorized(alice.AccessToken);
        using var bobClient = Authorized(bob.AccessToken);

        var key = $"scope-{Guid.NewGuid():N}";
        var orderRequest = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { productId, quantity = 1 }),
        };
        orderRequest.Headers.Add("Idempotency-Key", key);
        await aliceClient.SendAsync(orderRequest);
        Assert.Equal("completed", await WaitForCompletionAsync(aliceClient, key));

        var aliceOrders = await aliceClient.GetFromJsonAsync<MyOrdersResponse>("/orders/me");
        var bobOrders = await bobClient.GetFromJsonAsync<MyOrdersResponse>("/orders/me");

        Assert.Equal(1, aliceOrders!.Count);
        // Bob must not see Alice's order even though both are authenticated: the
        // query itself is the authorization boundary, with no resource handler.
        Assert.Equal(0, bobOrders!.Count);
    }

    [Fact]
    public async Task OrdersMe_WithoutAToken_IsUnauthorized()
    {
        var response = await _client.GetAsync("/orders/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousOrdering_StillWorks()
    {
        var productId = _productId;
        var key = $"anon-{Guid.NewGuid():N}";

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(new { productId, quantity = 1 }),
        };
        request.Headers.Add("Idempotency-Key", key);

        // No Authorization header: the pre-auth behaviour must survive the release
        // (ADR-013 §6) — the concurrency evidence depends on this path.
        var response = await _client.SendAsync(request);
        Assert.True(
            response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK,
            $"anonymous order was rejected: {(int)response.StatusCode}");

        // Fulfilment is checked against the database rather than over
        // GET /api/orders/{key}: that endpoint is now authenticated and
        // owner-scoped, and an anonymous order has no owner, so only STAFF/ADMIN
        // can poll it. The invariant under test is the ANONYMOUS PLACEMENT, and
        // this still proves the order reached the database.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        int? persistedOrderId = null;
        while (DateTime.UtcNow < deadline)
        {
            await using var db = _fx.CreateDbContext();
            persistedOrderId = await db.Orders
                .Where(o => o.IdempotencyKey == key)
                .Select(o => (int?)o.Id)
                .FirstOrDefaultAsync();
            if (persistedOrderId is not null) break;
            await Task.Delay(250);
        }

        Assert.True(persistedOrderId is not null, "the anonymous order never reached the database");
    }

    [Fact]
    public async Task Register_RejectsDuplicateEmail_AndDoesNotEnumerateAccounts()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        var duplicate = await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Casing must not create a second account for the same human.
        var shouty = await _client.PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest(email.ToUpperInvariant(), Password));
        Assert.Equal(HttpStatusCode.Conflict, shouty.StatusCode);

        // Wrong password and unknown email must be indistinguishable: same status
        // AND same message, or the endpoint becomes an account-enumeration oracle.
        var wrongPassword = await _client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, "definitely-the-wrong-password"));
        var unknownEmail = await _client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(NewEmail(), Password));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        var wrongBody = await wrongPassword.Content.ReadFromJsonAsync<ProblemBody>();
        var unknownBody = await unknownEmail.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal(wrongBody!.Error, unknownBody!.Error);
    }

    [Fact]
    public async Task Metrics_ExposeTheCountersTheFlowJustProduced()
    {
        var session = await RegisterAsync(NewEmail());
        using var authed = Authorized(session.AccessToken);
        await authed.GetAsync("/api/auth/me");
        await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(NewEmail(), "wrong-password-entirely"));

        // /internal/metrics is an ops read (it counts auth successes and failures),
        // so it is read as STAFF. Anonymous access here used to be asserted and was
        // the leak; the assertion that mattered — the counters this flow produced —
        // is unchanged.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/internal/metrics")).StatusCode);

        var staffLogin = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(StaffEmail, StaffPassword));
        Assert.Equal(HttpStatusCode.OK, staffLogin.StatusCode);
        var staff = Authorized((await staffLogin.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken);
        using (staff)
        {
            var metrics = await staff.GetStringAsync("/internal/metrics");

            Assert.Contains("flashsale.auth.registrations", metrics);
            Assert.Contains("reason=invalid_credentials", metrics);
            Assert.Contains("flashsale.http.request.duration", metrics);
        }
    }
}