using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpsBridge.Api.Auth;
using OpsBridge.Application.Connectors;
using OpsBridge.Domain.Connectors;

namespace OpsBridge.Api.Endpoints;

public static class ConnectorEndpoints
{
    public static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/connectors")
            .WithTags("Connectors")
            .RequireAuthorization(OpsBridgeAuthPolicies.AuditorOrAbove);

        group.MapGet("/", async (IConnectorRepository repo, CancellationToken ct) =>
        {
            var connectors = await repo.GetAllAsync(ct);
            var cloudBaseUrl = Environment.GetEnvironmentVariable("FACTORYFLOW_BASE_URL");

            var result = connectors.Select(c => new
            {
                Id = c.Id.ToString(),
                c.Name,
                Type = c.Type.ToString(),
                c.BaseUrl,
                c.IsActive,
                c.CreatedAt,
                c.UpdatedAt
            }).ToList();

            if (!string.IsNullOrWhiteSpace(cloudBaseUrl) &&
                !result.Any(c => c.Name.Contains("FactoryFlow", StringComparison.OrdinalIgnoreCase)))
            {
                result.Insert(0, new
                {
                    Id = "conn-factoryflow",
                    Name = "FactoryFlow REST Connector",
                    Type = ConnectorType.FactoryFlowRest.ToString(),
                    BaseUrl = (string?)cloudBaseUrl,
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = (DateTimeOffset?)DateTimeOffset.UtcNow
                });
            }

            return Results.Ok(result);
        })
        .RequireAuthorization(OpsBridgeAuthPolicies.AuditorOrAbove)
        .WithName("ListConnectors");

        group.MapPost("/{id}/ping", async (string id, IConnectorRepository repo, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
        {
            if (id != "conn-factoryflow")
            {
                if (!Guid.TryParse(id, out var connectorId))
                {
                    return Results.BadRequest(new { message = "Invalid connector ID." });
                }

                var connector = await repo.GetByIdAsync(connectorId, ct);
                if (connector == null)
                {
                    return Results.NotFound(new { message = "Connector not found." });
                }

                if (!connector.IsActive || connector.Type != ConnectorType.FactoryFlowRest)
                {
                    return Results.Conflict(new { message = "FactoryFlow REST connector is not active." });
                }
            }

            // The database BaseUrl is editable by an operator. Never use it as an
            // outbound request destination: the only target is deployment config.
            var configuredBaseUrl = Environment.GetEnvironmentVariable("FACTORYFLOW_BASE_URL");
            if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var target) ||
                target.Scheme != Uri.UriSchemeHttps || target.Port != 443 ||
                !string.IsNullOrEmpty(target.UserInfo) ||
                !string.IsNullOrEmpty(target.Query) ||
                !string.IsNullOrEmpty(target.Fragment))
            {
                return Results.Json(new { message = "FactoryFlow HTTPS URL is not configured." }, statusCode: 503);
            }

            using var httpClient = httpClientFactory.CreateClient("factoryflow-ping");
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var response = await httpClient.GetAsync(new Uri(target, $"{target.AbsolutePath.TrimEnd('/')}/health"), ct);
                stopwatch.Stop();
                if (!response.IsSuccessStatusCode)
                {
                    return Results.Json(new { message = $"Connector returned HTTP {(int)response.StatusCode}." }, statusCode: 502);
                }

                return Results.Ok(new
                {
                    success = true,
                    connectorId = id,
                    latencyMs = (int)stopwatch.ElapsedMilliseconds,
                    message = "Connector health endpoint responded successfully.",
                    targetUrl = target.ToString(),
                    checkedAt = DateTimeOffset.UtcNow
                });
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { message = "Connector could not be reached." }, statusCode: 502);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Json(new { message = "Connector health check timed out." }, statusCode: 504);
            }
        })
        .RequireAuthorization(OpsBridgeAuthPolicies.AuditorOrAbove)
        .WithName("PingConnector");

        group.MapGet("/{id:guid}", async (Guid id, IConnectorRepository repo, CancellationToken ct) =>
        {
            var connector = await repo.GetByIdAsync(id, ct);
            if (connector == null)
            {
                return Results.NotFound(new { error = "NotFound", message = $"Connector with ID {id} not found." });
            }

            return Results.Ok(new
            {
                connector.Id,
                connector.Name,
                Type = connector.Type.ToString(),
                connector.BaseUrl,
                connector.IsActive,
                connector.CreatedAt,
                connector.UpdatedAt
            });
        })
        .RequireAuthorization(OpsBridgeAuthPolicies.AuditorOrAbove)
        .WithName("GetConnectorById");

        group.MapPost("/", async (SaveConnectorDto dto, IConnectorRepository repo, CancellationToken ct) =>
        {
            var connector = new ConnectorConfig
            {
                Id = dto.Id ?? Guid.NewGuid(),
                Name = dto.Name,
                Type = dto.Type,
                Secret = dto.Secret,
                BaseUrl = dto.BaseUrl,
                IsActive = dto.IsActive ?? true
            };

            await repo.SaveAsync(connector, ct);

            return Results.Created($"/api/v1/connectors/{connector.Id}", new
            {
                connector.Id,
                connector.Name,
                Type = connector.Type.ToString(),
                connector.IsActive
            });
        })
        .RequireAuthorization(OpsBridgeAuthPolicies.OperatorOrAdmin)
        .WithName("SaveConnector");

        return app;
    }
}

public sealed record SaveConnectorDto(
    Guid? Id,
    string Name,
    ConnectorType Type,
    string Secret,
    string? BaseUrl,
    bool? IsActive
);
