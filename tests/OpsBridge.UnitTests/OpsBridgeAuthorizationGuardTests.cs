using System.Text.RegularExpressions;
using Xunit;

namespace OpsBridge.UnitTests;

public sealed record OpsBridgeRoute(string Method, string Template, bool IsAuthorized, string File)
{
    public string Display => $"{Method} {Template}";
}

public partial class OpsBridgeAuthorizationGuardTests
{
    private static readonly string[] RouteFiles =
    [
        Path.Combine("src", "OpsBridge.Api", "Program.cs"),
        Path.Combine("src", "OpsBridge.Api", "Endpoints", "AuthEndpoints.cs"),
        Path.Combine("src", "OpsBridge.Api", "Endpoints", "ConnectorEndpoints.cs"),
        Path.Combine("src", "OpsBridge.Api", "Endpoints", "IngestionEndpoints.cs"),
        Path.Combine("src", "OpsBridge.Api", "Endpoints", "OutboxEndpoints.cs"),
        Path.Combine("src", "OpsBridge.Api", "Endpoints", "ReconciliationEndpoints.cs"),
        Path.Combine("src", "OpsBridge.Api", "Endpoints", "ReplayEndpoints.cs"),
        Path.Combine("src", "OpsBridge.Api", "Endpoints", "WebhookEndpoints.cs"),
    ];

    private static readonly Dictionary<string, string> PublicRoutes = new(StringComparer.Ordinal)
    {
        ["POST /api/v1/auth/login"] = "Anonymous login endpoint; accepts credentials and returns a JWT.",
        ["GET /healthz"] = "Liveness probe; must answer without credentials.",
        ["GET /health/messaging"] = "Dependency status only; reveals no broker URL or business identifiers.",
        ["GET /metrics"] = "Aggregate Prometheus scrape endpoint; no business identifiers or auth counters.",
        ["POST /api/v1/webhooks/factoryflow"] = "External webhook; protected by Svix/HMAC signature, not bearer token.",
    };

    [Fact]
    public void EveryMappedRoute_IsAuthorized_Or_InPublicAllowlist()
    {
        var unauthorized = ReadRoutes()
            .Where(r => !r.IsAuthorized)
            .Select(r => r.Display)
            .Except(PublicRoutes.Keys, StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unauthorized.Length == 0,
            "Unauthorized OpsBridge routes not in public allowlist:\n  " + string.Join("\n  ", unauthorized));
    }

    [Fact]
    public void PublicAllowlist_HasNoStaleEntries()
    {
        var mapped = ReadRoutes().Select(r => r.Display).ToHashSet(StringComparer.Ordinal);
        var stale = PublicRoutes.Keys.Where(k => !mapped.Contains(k)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0, "Stale allowlist entries:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void UnmigratedProcurementLagDraft_IsNotExposedAsAnApiRoute()
    {
        // The draft detector writes alert rows and has no accepted migration or
        // concurrency test. In particular, GET must never trigger the scan.
        var exposed = ReadRoutes()
            .Where(route => route.Template.StartsWith("/api/v1/reconciliation/stuck-orders", StringComparison.Ordinal))
            .Select(route => route.Display)
            .ToArray();

        Assert.Empty(exposed);
    }

    private static List<OpsBridgeRoute> ReadRoutes() =>
        RouteFiles.Select(relative => Path.Combine(RepositoryRoot(), relative)).SelectMany(Scan).ToList();

    private static IEnumerable<OpsBridgeRoute> Scan(string path)
    {
        var source = File.ReadAllText(path).Replace("\r\n", "\n");
        var file = Path.GetFileName(path);
        var maps = MapCallRegex().Matches(source).Select(m => new
        {
            Start = m.Index,
            Receiver = m.Groups["receiver"].Value,
            Verb = m.Groups["verb"].Value.ToUpperInvariant(),
            Route = m.Groups["route"].Value,
        }).Concat(MapMetricsRegex().Matches(source).Select(m => new
        {
            Start = m.Index,
            Receiver = "app",
            Verb = "GET",
            Route = m.Groups["route"].Value,
        })).OrderBy(m => m.Start).ToList();

        for (var i = 0; i < maps.Count; i++)
        {
            var end = i + 1 < maps.Count ? maps[i + 1].Start : source.Length;
            var metadata = source[maps[i].Start..end];
            var prefix = maps[i].Receiver == "group" ? GroupPrefixBefore(source, maps[i].Start) : string.Empty;
            yield return new OpsBridgeRoute(maps[i].Verb, prefix + maps[i].Route,
                metadata.Contains("RequireAuthorization", StringComparison.Ordinal), file);
        }
    }

    private static string GroupPrefixBefore(string source, int index)
    {
        var last = GroupRegex().Matches(source).Cast<Match>().LastOrDefault(m => m.Index < index);
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
            $"Could not find AzureFlashSale.slnx walking up from {AppContext.BaseDirectory}.")).FullName;
    }

    [GeneratedRegex(@"\b(?<receiver>app|group)\.Map(?<verb>Get|Post|Put|Patch|Delete)\(\s*""(?<route>[^""]*)""")]
    private static partial Regex MapCallRegex();
    [GeneratedRegex(@"\bapp\.MapMetrics\(\s*""(?<route>[^""]*)""")]
    private static partial Regex MapMetricsRegex();
    [GeneratedRegex(@".MapGroup\(\s*""(?<prefix>[^""]*)""")]
    private static partial Regex GroupRegex();
}
