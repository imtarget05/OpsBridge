extern alias order;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FlashSale.Application.Auth;

using Program = order.Program;
using FlashSale.Domain;
using FlashSale.Domain.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.IntegrationTests;

/// <summary>
/// The 401/403 contract for the endpoints that used to be wide open.
/// <para>
/// Before this, <c>POST /api/orders/{key}/pay</c> would drive ANY order to
/// Confirmed, and the four <c>/internal/**</c> scans would cancel orders and
/// rewrite Redis stock — all with no <c>Authorization</c> header at all. The unit
/// suite (<c>EndpointAuthorizationGuardTests</c>) proves no route is mapped without
/// a policy; this file proves the policy actually rejects, through the real
/// middleware pipeline and the real JWT validation.
/// </para>
/// <para>
/// It also exercises the escape hatches, because a policy nobody can satisfy is
/// just an outage: the STAFF account seeded from <c>Bootstrap:StaffPassword</c> and
/// the operator bootstrap both mint tokens that DO reach these endpoints.
/// </para>
/// </summary>
[Collection("flashsale")]
public sealed class EndpointAuthorizationTests : IAsyncLifetime
{
    private const string CustomerPassword = "correct-horse-battery-staple";

    /// <summary>Seeds the STAFF account the runbook endpoints are meant for.</summary>
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
    private int _productId;

    public EndpointAuthorizationTests(FlashSaleFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        _productId = await _fx.ResetDatabaseAsync(stock: 25);

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

    /// <summary>
    /// Environment variables, not <c>ConfigureAppConfiguration</c>: this app is
    /// minimal-hosting, and those callbacks only replay after Program.cs has
    /// already read configuration. Same constraint as AuthFlowTests.
    /// </summary>
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

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private HttpClient Authorized(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<AuthResponse> RegisterAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"sec+{Guid.NewGuid():N}@example.com", CustomerPassword));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>
    /// The user id behind a session, read from the database. Deliberately not
    /// decoded out of the JWT: the handler's inbound claim mapping rewrites
    /// <c>sub</c> on read, and a test that depends on that would be asserting a
    /// serializer detail instead of an authorization rule.
    /// </summary>
    private async Task<Guid> UserIdOfAsync(AuthResponse session)
    {
        await using var db = _fx.CreateDbContext();
        return await db.Users.Where(u => u.Email == session.Email).Select(u => u.Id).FirstAsync();
    }

    /// <summary>Login as the STAFF account the seeder created from configuration.</summary>
    private async Task<string> StaffTokenAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(StaffEmail, StaffPassword));

        // A failure here means the STAFF seed did not happen, i.e. the ops surface
        // is unreachable — which is the failure mode worth catching loudly.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
    }

    private async Task<int> CreatePendingOrderAsync(string key, Guid? userId)
    {
        await using var db = _fx.CreateDbContext();
        var order = new FlashSale.Domain.Entities.Order
        {
            ProductId = _productId,
            Quantity = 1,
            IdempotencyKey = key,
            CreatedAt = DateTime.UtcNow,
            Status = OrderStatus.PendingPayment,
            PaymentDueAt = DateTimeOffset.UtcNow,
            UserId = userId,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<OrderStatus> StatusOfAsync(int orderId)
    {
        await using var db = _fx.CreateDbContext();
        return await db.Orders.Where(o => o.Id == orderId).Select(o => o.Status).FirstAsync();
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return client.SendAsync(request);
    }

    private static object PayBody => new { outcome = "completed" };

    // ---------------------------------------------------------------
    // POST /api/orders/{key}/pay — the money endpoint
    // ---------------------------------------------------------------

    [Fact]
    public async Task Pay_WithoutAToken_IsUnauthorized()
    {
        var key = $"pay-anon-{Guid.NewGuid():N}";
        await CreatePendingOrderAsync(key, userId: null);

        var response = await _client.PostAsJsonAsync($"/api/orders/{key}/pay", PayBody);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pay_ByAnotherCustomer_IsForbidden_AndChangesNothing()
    {
        var owner = await RegisterAsync();
        var stranger = await RegisterAsync();
        var key = $"pay-other-{Guid.NewGuid():N}";
        var orderId = await CreatePendingOrderAsync(key, await UserIdOfAsync(owner));

        using var client = Authorized(stranger.AccessToken);
        var response = await client.PostAsJsonAsync($"/api/orders/{key}/pay", PayBody);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // A refusal must be inert: no transition happened behind the 403.
        Assert.Equal(OrderStatus.PendingPayment, await StatusOfAsync(orderId));
    }

    [Fact]
    public async Task Pay_ByTheOwner_ConfirmsTheOrder()
    {
        var owner = await RegisterAsync();
        var key = $"pay-own-{Guid.NewGuid():N}";
        var orderId = await CreatePendingOrderAsync(key, await UserIdOfAsync(owner));

        using var client = Authorized(owner.AccessToken);
        var response = await client.PostAsJsonAsync($"/api/orders/{key}/pay", PayBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OrderStatus.Confirmed, await StatusOfAsync(orderId));
    }

    [Fact]
    public async Task Pay_ByStaff_IsAllowedOnAnOrderTheyDoNotOwn()
    {
        var owner = await RegisterAsync();
        var key = $"pay-staff-{Guid.NewGuid():N}";
        var orderId = await CreatePendingOrderAsync(key, await UserIdOfAsync(owner));

        using var client = Authorized(await StaffTokenAsync());
        var response = await client.PostAsJsonAsync($"/api/orders/{key}/pay", PayBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OrderStatus.Confirmed, await StatusOfAsync(orderId));
    }

    // ---------------------------------------------------------------
    // GET /api/orders/{key} — ownership on the read side too
    // ---------------------------------------------------------------

    [Fact]
    public async Task OrderStatus_WithoutAToken_IsUnauthorized()
    {
        var key = $"get-anon-{Guid.NewGuid():N}";
        await CreatePendingOrderAsync(key, userId: null);

        var response = await _client.GetAsync($"/api/orders/{key}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OrderStatus_ByAnotherCustomer_IsForbidden()
    {
        var owner = await RegisterAsync();
        var stranger = await RegisterAsync();
        var key = $"get-other-{Guid.NewGuid():N}";
        await CreatePendingOrderAsync(key, await UserIdOfAsync(owner));

        using var client = Authorized(stranger.AccessToken);
        var response = await client.GetAsync($"/api/orders/{key}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OrderStatus_ByTheOwner_ReturnsTheOrder()
    {
        var owner = await RegisterAsync();
        var key = $"get-own-{Guid.NewGuid():N}";
        await CreatePendingOrderAsync(key, await UserIdOfAsync(owner));

        using var client = Authorized(owner.AccessToken);
        var response = await client.GetAsync($"/api/orders/{key}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(key, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OrderStatus_ByStaff_IsAllowed()
    {
        var key = $"get-staff-{Guid.NewGuid():N}";
        await CreatePendingOrderAsync(key, userId: null);

        using var client = Authorized(await StaffTokenAsync());
        var response = await client.GetAsync($"/api/orders/{key}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // The ops runbook surface
    // ---------------------------------------------------------------

    /// <summary>
    /// The state-mutating endpoints that require STAFF/ADMIN. Saga checkout is
    /// deliberately absent: it is authenticated rather than staff-only (it is the
    /// customer checkout path), and it has its own test below.
    /// </summary>
    public static TheoryData<string, string, string?> StaffOnlyEndpoints => new()
    {
        { "POST", "/internal/automation/payment-timeout-scan", null },
        { "POST", "/internal/automation/low-stock-scan", null },
        { "POST", "/internal/automation/daily-report", null },
        { "POST", "/api/outbox/enqueue", "{}" },
        { "POST", "/api/outbox/requeue", null },
    };

    [Theory]
    [MemberData(nameof(StaffOnlyEndpoints))]
    public async Task StaffOnlyEndpoint_WithoutAToken_IsUnauthorized(string method, string url, string? body)
    {
        var response = await SendAsync(_client, method, url, body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(StaffOnlyEndpoints))]
    public async Task StaffOnlyEndpoint_WithACustomerToken_IsForbidden(string method, string url, string? body)
    {
        var session = await RegisterAsync();
        using var client = Authorized(session.AccessToken);

        var response = await SendAsync(client, method, url, body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InboxConsume_WithoutAToken_IsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/inbox/consume", new { messageId = Guid.NewGuid(), consumerName = "sec-test" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InboxConsume_WithACustomerToken_IsForbidden()
    {
        var session = await RegisterAsync();
        using var client = Authorized(session.AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/inbox/consume", new { messageId = Guid.NewGuid(), consumerName = "sec-test" });

        // A CUSTOMER must not be able to poison the dedup ledger that the real
        // consumers read: that is a silent-drop primitive, not a customer feature.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ResyncStock_RequiresStaff()
    {
        var url = $"/internal/resync-stock/{_productId}";
        var session = await RegisterAsync();
        using var asCustomer = Authorized(session.AccessToken);
        using var asStaff = Authorized(await StaffTokenAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync(url, content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asCustomer.PostAsync(url, content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await asStaff.PostAsync(url, content: null)).StatusCode);
    }

    [Fact]
    public async Task InternalReads_RequireStaff()
    {
        var session = await RegisterAsync();
        using var asCustomer = Authorized(session.AccessToken);
        using var asStaff = Authorized(await StaffTokenAsync());

        string[] urls =
        [
            "/internal/metrics",
            "/internal/automation/summary",
            "/internal/automation/alerts",
            "/internal/automation/daily-report/latest",
        ];

        foreach (var url in urls)
        {
            var anonymous = await _client.GetAsync(url);
            var forbidden = await asCustomer.GetAsync(url);
            var allowed = await asStaff.GetAsync(url);

            Assert.True(
                anonymous.StatusCode is HttpStatusCode.Unauthorized,
                $"{url} answered {(int)anonymous.StatusCode} anonymously");
            Assert.True(
                forbidden.StatusCode is HttpStatusCode.Forbidden,
                $"{url} answered {(int)forbidden.StatusCode} to a CUSTOMER");
            // "Reachable", not "2xx": /internal/automation/daily-report/latest is a
            // legitimate 404 until a report has been generated, and the claim under
            // test is only that the authorization gate let STAFF through.
            Assert.True(
                allowed.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                $"{url} answered {(int)allowed.StatusCode} to STAFF");
        }
    }

    [Fact]
    public async Task OutboxRunbook_IsReachableByStaff()
    {
        using var client = Authorized(await StaffTokenAsync());

        var enqueue = await client.PostAsJsonAsync("/api/outbox/enqueue", new { });
        var requeue = await client.PostAsync("/api/outbox/requeue", content: null);
        var consume = await client.PostAsJsonAsync(
            "/api/inbox/consume", new { messageId = Guid.NewGuid(), consumerName = "sec-test" });

        Assert.True(enqueue.IsSuccessStatusCode, $"enqueue: {(int)enqueue.StatusCode}");
        Assert.True(requeue.IsSuccessStatusCode, $"requeue: {(int)requeue.StatusCode}");
        Assert.True(consume.IsSuccessStatusCode, $"inbox consume: {(int)consume.StatusCode}");
    }

    [Fact]
    public async Task SagaCheckout_RequiresAuthentication_ButNotTheStaffRole()
    {
        var body = new { productId = _productId, quantity = 1, amount = 1m };
        var session = await RegisterAsync();
        using var asCustomer = Authorized(session.AccessToken);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _client.PostAsJsonAsync("/api/saga/checkout", body)).StatusCode);

        // A registered customer gets past authorization. The saga then fails
        // against the (absent) Payment.Service and compensates, so the status is
        // about the saga, not about the gate — the point is that it is NOT 401/403.
        var asCustomerResponse = await asCustomer.PostAsJsonAsync("/api/saga/checkout", body);
        Assert.True(
            asCustomerResponse.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
            $"a CUSTOMER was refused the checkout path: {(int)asCustomerResponse.StatusCode}");
    }

    // ---------------------------------------------------------------
    // Role assignment
    // ---------------------------------------------------------------

    [Fact]
    public async Task Register_IgnoresARoleInTheBody()
    {
        var email = $"escalate+{Guid.NewGuid():N}@example.com";

        // Raw JSON, not an anonymous object: two members differing only in case
        // collide in the serializer's property table, and the point of the test is
        // what the SERVER does with `role`, not what the client can serialize.
        using var body = new StringContent(
            $$"""{"email":"{{email}}","password":"{{CustomerPassword}}","role":"{{AuthRoles.Admin}}"}""",
            System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/auth/register", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(AuthRoles.Customer, session.Role);

        // The role claim in the token agrees, so this is not a response-only lie.
        using var client = Authorized(session.AccessToken);
        Assert.Contains(AuthRoles.Customer, await client.GetStringAsync("/api/auth/me"));
    }

    [Fact]
    public async Task Bootstrap_WithoutTheHeader_IsForbidden()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/auth/bootstrap",
            new { email = $"root+{Guid.NewGuid():N}@example.com", password = CustomerPassword });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_WithAWrongToken_IsForbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/bootstrap")
        {
            Content = JsonContent.Create(
                new { email = $"root+{Guid.NewGuid():N}@example.com", password = CustomerPassword }),
        };
        request.Headers.Add("X-Bootstrap-Token", "not-the-configured-token");

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Bootstrap_WithTheConfiguredToken_CreatesAnAdmin_WhoCanThenReachOpsEndpoints()
    {
        var email = $"root+{Guid.NewGuid():N}@example.com";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/bootstrap")
        {
            Content = JsonContent.Create(new { email, password = CustomerPassword }),
        };
        request.Headers.Add("X-Bootstrap-Token", BootstrapToken);

        var created = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var session = (await created.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(AuthRoles.Admin, session.Role);

        // The point of the whole mechanism: the ops surface is now reachable.
        using var client = Authorized(session.AccessToken);
        var scan = await client.PostAsync("/internal/automation/payment-timeout-scan", content: null);
        Assert.True(scan.IsSuccessStatusCode, $"the bootstrapped admin was refused: {(int)scan.StatusCode}");

        // And the new admin is a real account, not a one-shot token.
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, CustomerPassword));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_CannotSeizeAnAlreadyRegisteredAccount()
    {
        var email = $"target+{Guid.NewGuid():N}@example.com";
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, CustomerPassword));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/bootstrap")
        {
            Content = JsonContent.Create(new { email, password = CustomerPassword }),
        };
        request.Headers.Add("X-Bootstrap-Token", BootstrapToken);

        Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(request)).StatusCode);

        // The original owner is still a CUSTOMER, so nobody gained a role.
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, CustomerPassword));
        var session = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;
        Assert.Equal(AuthRoles.Customer, session.Role);
    }
}