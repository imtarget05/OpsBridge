using System.Text.RegularExpressions;

namespace FlashSale.UnitTests;

/// <summary>One route registration found in the composition root.</summary>
public sealed record RouteRegistration(string Method, string Template, bool IsAuthorized, string File)
{
    public string Display => $"{Method} {Template}";

    public override string ToString() => Display;
}

/// <summary>
/// Regression guard for the authorization hole this repo shipped with: the
/// <c>StaffOrAdmin</c> policy existed and was registered, but NOTHING referenced
/// it, and nine state-mutating endpoints (payment capture, the three automation
/// scans, the daily report, the Redis stock resync, the outbox/inbox runbooks)
/// were reachable with no token at all.
///
/// The rule enforced here is a DENY-BY-DEFAULT allowlist: every route the
/// composition root maps must either call <c>RequireAuthorization()</c> or appear
/// in <see cref="PublicRoutes"/> with a written justification. Adding an endpoint
/// and forgetting the policy fails this test instead of shipping a hole.
/// </summary>
/// <remarks>
/// The routes are read from the source of <c>Program.cs</c> /
/// <c>AuthEndpoints.cs</c> rather than from a booted host: booting the real
/// composition root needs PostgreSQL, Redis and a queue, which would make this a
/// slow integration test guarding a pure bookkeeping rule. What it does verify is
/// exactly the thing that regressed — a route registered without a policy next to
/// it in the source.
/// </remarks>
public partial class EndpointAuthorizationGuardTests
{
    /// <summary>Source files that map routes, relative to the repository root.</summary>
    private static readonly string[] RouteFiles =
    [
        Path.Combine("src", "Order.Api", "Program.cs"),
        Path.Combine("src", "Order.Api", "Auth", "AuthEndpoints.cs"),
    ];

    /// <summary>
    /// The complete set of routes that are INTENTIONALLY reachable without a
    /// token. Anything not listed here must be authorized. Every entry carries its
    /// reason, and <see cref="PublicAllowlist_HasNoStaleEntries"/> fails if an entry
    /// stops matching a real route, so the list cannot quietly become a graveyard
    /// that hides a rename.
    /// </summary>
    private static readonly Dictionary<string, string> PublicRoutes = new(StringComparer.Ordinal)
    {
        // Anonymous ORDER PLACEMENT is a documented product decision (ADR-013 §6)
        // and the zero-oversell evidence in load-tests/ depends on it: the harness
        // fires 50 unauthenticated requests and asserts exactly `stock` succeed.
        ["POST /api/orders"] =
            "Anonymous checkout is a documented decision (ADR-013 §6); the zero-oversell evidence depends on it.",

        // Catalogue browse: public data, no PII, no mutation. The benchmark and
        // load harnesses read it without a token.
        ["GET /api/products/{id}"] =
            "Public catalogue read; exposes no user data and mutates nothing.",

        // Orchestrator probes. If these needed a token, a kubelet would report the
        // pod unhealthy during exactly the incident it is meant to observe.
        ["GET /health/live"] = "Liveness probe; must answer without credentials.",
        ["GET /healthz"] = "Liveness probe alias; must answer without credentials.",
        ["GET /health/ready"] = "Readiness probe; must answer without credentials.",

        // The credential entry points. Each is a gate, not a resource: they
        // cannot be used to read or mutate anything on their own.
        ["POST /api/auth/register"] = "Creates a CUSTOMER account only; the role is never read from the body.",
        ["POST /api/auth/login"] = "Exchanges credentials for a token pair; proves nothing on its own.",
        ["POST /api/auth/refresh"] = "Exchanges a refresh token for a new pair; proves nothing on its own.",

        // Anonymous by necessity (there is no admin yet) and gated by an
        // operator-held secret instead: 404 when Bootstrap:AdminToken is unset,
        // 403 on a wrong secret. Asserted end-to-end by
        // FlashSale.IntegrationTests.EndpointAuthorizationTests.
        ["POST /api/auth/bootstrap"] =
            "Secret-gated first-admin bootstrap; disabled (404) unless Bootstrap:AdminToken is configured.",

        // Prometheus scrape endpoint. Unauthenticated by design per the Prometheus
        // scrape convention (scrapers carry no credentials), and it deliberately
        // excludes the auth.* counters that /internal/metrics exposes to staff.
        ["GET /metrics"] =
            "Public Prometheus text exposition; carries no auth counters and requires no credentials (Prometheus scrape convention).",
    };

    // These routes use a dedicated, fail-closed HMAC service credential instead
    // of a user JWT. They are not public and must keep their verifier in the
    // route body; the integration test also proves unsigned requests get 401.
    private static readonly HashSet<string> HmacProtectedRoutes = new(StringComparer.Ordinal)
    {
        "GET /internal/agent/outbox/stuck",
        "GET /internal/agent/outbox/pending",
        "GET /internal/agent/outbox/{messageId:guid}",
        "POST /internal/agent/outbox/{messageId:guid}/requeue",
    };

    /// <summary>HTTP verbs that can change state.</summary>
    private static readonly string[] MutatingVerbs = ["POST", "PUT", "PATCH", "DELETE"];

    [Fact]
    public void EveryMappedRoute_IsEitherAuthorizedOrDeliberatelyPublic()
    {
        var routes = ReadRoutes();
        Assert.NotEmpty(routes);

        var holes = routes
            .Where(r => !r.IsAuthorized && !PublicRoutes.ContainsKey(r.Display))
            .Select(r => $"{r.Display}  ({r.File})")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            holes.Length == 0,
            "These routes are mapped without RequireAuthorization() and are not in the "
            + "public allowlist. Add the policy, or add the route to PublicRoutes with a "
            + "written justification:\n  " + string.Join("\n  ", holes));
    }

    [Fact]
    public void NoStateMutatingRoute_IsPublic()
    {
        // Stronger than the check above, and independent of it: a POST/PUT/PATCH/
        // DELETE reachable without a token is a hole by definition, so the
        // allowlist may not contain one at all.
        var holes = ReadRoutes()
            .Where(r => MutatingVerbs.Contains(r.Method) && PublicRoutes.ContainsKey(r.Display))
            .Select(r => r.Display)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        // The mutating public routes are exactly: anonymous ORDER PLACEMENT (the
        // documented product decision the zero-oversell evidence depends on) and
        // the four credential entry points, none of which can touch an existing
        // resource. Enumerated so that ADDING one fails this test loudly instead
        // of riding in on the allowlist.
        Assert.Equal(
            new[]
            {
                "POST /api/auth/bootstrap",
                "POST /api/auth/login",
                "POST /api/auth/refresh",
                "POST /api/auth/register",
                "POST /api/orders",
            }.OrderBy(x => x, StringComparer.Ordinal),
            holes.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Theory]
    // The nine endpoints that were open before this guard existed, plus the reads
    // that leaked operational state. Each must now carry a policy.
    [InlineData("POST /api/orders/{idempotencyKey}/pay")]
    [InlineData("POST /internal/automation/payment-timeout-scan")]
    [InlineData("POST /internal/automation/low-stock-scan")]
    [InlineData("POST /internal/automation/daily-report")]
    [InlineData("POST /internal/resync-stock/{productId}")]
    [InlineData("POST /api/saga/checkout")]
    [InlineData("POST /api/outbox/enqueue")]
    [InlineData("POST /api/outbox/requeue")]
    [InlineData("POST /api/inbox/consume")]
    [InlineData("GET /internal/metrics")]
    [InlineData("GET /internal/automation/summary")]
    [InlineData("GET /internal/automation/alerts")]
    [InlineData("GET /internal/automation/stuck-orders")]
    [InlineData("POST /internal/automation/stuck-order-scan")]
    [InlineData("GET /api/orders/{idempotencyKey}")]
    [InlineData("GET /api/saga/{idempotencyKey}")]
    public void PreviouslyOpenRoute_IsNowAuthorized(string display)
    {
        var route = Assert.Single(ReadRoutes(), r => r.Display == display);
        Assert.True(route.IsAuthorized, $"{display} is mapped with no authorization requirement.");
    }

    [Fact]
    public void PublicAllowlist_HasNoStaleEntries()
    {
        var mapped = ReadRoutes().Select(r => r.Display).ToHashSet(StringComparer.Ordinal);

        var stale = PublicRoutes.Keys.Where(k => !mapped.Contains(k))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            stale.Length == 0,
            "These allowlist entries no longer match any mapped route. A stale entry means "
            + "the route was renamed and the new name is unchecked:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void HmacRouteAllowlist_HasNoStaleEntries()
    {
        var mapped = ReadRoutes().Select(r => r.Display).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(HmacProtectedRoutes, route => !mapped.Contains(route));
    }

    [Fact]
    public void EveryPublicRoute_CarriesAJustification()
    {
        var undocumented = PublicRoutes
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => kv.Key)
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            "Every public route needs a written reason:\n  " + string.Join("\n  ", undocumented));
    }

    // ---------------------------------------------------------------
    // Source scan
    // ---------------------------------------------------------------

    private static List<RouteRegistration> ReadRoutes() =>
        RouteFiles
            .Select(relative => Path.Combine(RepositoryRoot(), relative))
            .SelectMany(Scan)
            .ToList();

    /// <summary>
    /// Parse one file into its route registrations. An endpoint is "authorized"
    /// when <c>RequireAuthorization</c> appears between its <c>Map*</c> call and
    /// the next <c>Map*</c> call in the same file — i.e. in the metadata chain
    /// that belongs to it, not in a later endpoint's.
    /// </summary>
    private static IEnumerable<RouteRegistration> Scan(string path)
    {
        var source = File.ReadAllText(path).Replace("\r\n", "\n");
        var file = Path.GetFileName(path);

        var maps = MapCallRegex().Matches(source).Select(m => new
        {
            Start = m.Index,
            Receiver = m.Groups["receiver"].Value,
            Verb = m.Groups["verb"].Value.ToUpperInvariant(),
            Route = m.Groups["route"].Value,
        }).ToList();

        for (var i = 0; i < maps.Count; i++)
        {
            var end = i + 1 < maps.Count ? maps[i + 1].Start : source.Length;
            var metadata = source[maps[i].Start..end];

            // A group-relative route ("/login") is resolved against the prefix of
            // the MapGroup that declared it, so the guard compares full paths.
            var prefix = maps[i].Receiver == "group" ? GroupPrefixBefore(source, maps[i].Start) : string.Empty;
            var template = prefix + maps[i].Route;

            yield return new RouteRegistration(
                maps[i].Verb,
                template,
                metadata.Contains("RequireAuthorization", StringComparison.Ordinal)
                || (HmacProtectedRoutes.Contains($"{maps[i].Verb} {template}")
                    && metadata.Contains("AgentRecoveryAuth.VerifyAsync(http.Request, key, ct)", StringComparison.Ordinal)),
                file);
        }
    }

    private static string GroupPrefixBefore(string source, int index)
    {
        var last = GroupRegex().Matches(source)
            .Cast<Match>()
            .LastOrDefault(m => m.Index < index);

        return last?.Groups["prefix"].Value ?? string.Empty;
    }

    private static DirectoryInfo? _root;

    private static string RepositoryRoot()
    {
        if (_root is not null) return _root.FullName;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AzureFlashSale.slnx")))
            dir = dir.Parent;

        return (_root = dir ?? throw new InvalidOperationException(
            $"Could not find AzureFlashSale.slnx walking up from {AppContext.BaseDirectory}. " +
            "The guard test must run inside the repository.")).FullName;
    }

    // `app.MapGet("/x", ...)` and `group.MapPost("/y", ...)`; MapGroup and
    // MapOpenApi deliberately do not match (no verb segment / no route literal).
    [GeneratedRegex(@"\b(?<receiver>app|group)\.Map(?<verb>Get|Post|Put|Patch|Delete)\(\s*""(?<route>[^""]*)""")]
    private static partial Regex MapCallRegex();

    [GeneratedRegex(@"\.MapGroup\(\s*""(?<prefix>[^""]*)""")]
    private static partial Regex GroupRegex();
}
