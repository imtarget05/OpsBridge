using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpsBridge.Api.Auth;
using OpsBridge.Application.AI;
using OpsBridge.Application.UseCases;
using OpsBridge.Domain;

namespace OpsBridge.Api.Endpoints;

public static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ai")
            .WithTags("AI Assistant")
            .RequireAuthorization();

        // POST /api/v1/ai/chat - Chat with AI assistant
        group.MapPost("/chat", async (
            OpsBridge.Application.UseCases.AiChatRequest request,
            HttpContext context,
            AiChatUseCase aiChatUseCase,
            CancellationToken ct) =>
        {
            // Get user identity from JWT claims (not from request body)
            var user = context.User;
            var userId = user.Identity?.Name ?? user.FindFirst("sub")?.Value ?? "Unknown";
            var role = user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                ?? (user.IsInRole(OpsBridgeRoles.Admin) ? OpsBridgeRoles.Admin :
                    user.IsInRole(OpsBridgeRoles.FinanceReviewer) ? OpsBridgeRoles.FinanceReviewer :
                    user.IsInRole(OpsBridgeRoles.Operator) ? OpsBridgeRoles.Operator :
                    user.IsInRole(OpsBridgeRoles.Auditor) ? OpsBridgeRoles.Auditor : "");

            // Validate role - only staff roles allowed
            if (!IsStaffRole(role))
            {
                return Results.Json(new AiErrorResponse(
                    "Forbidden",
                    "AI assistant access requires staff role (Operator, FinanceReviewer, or Admin)",
                    "AI_FORBIDDEN_ROLE"), statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var response = await aiChatUseCase.ExecuteAsync(request, userId, role, ct);
                return Results.Ok(new AiChatResponseDto(
                    response.Response,
                    response.ConversationId,
                    response.Provider,
                    response.TokensUsed,
                    response.IsFallback));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new AiErrorResponse(
                    "Unauthorized",
                    ex.Message,
                    "AI_UNAUTHORIZED"), statusCode: StatusCodes.Status401Unauthorized);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new AiErrorResponse(
                    "BadRequest",
                    ex.Message,
                    "AI_INVALID_REQUEST"), statusCode: StatusCodes.Status400BadRequest);
            }
            catch (AiProviderException ex)
            {
                var statusCode = ex.HttpStatusCode switch
                {
                    401 => StatusCodes.Status401Unauthorized,
                    403 => StatusCodes.Status403Forbidden,
                    429 => StatusCodes.Status429TooManyRequests,
                    503 => StatusCodes.Status503ServiceUnavailable,
                    504 => StatusCodes.Status504GatewayTimeout,
                    _ => StatusCodes.Status502BadGateway
                };

                return Results.Json(new AiErrorResponse(
                    "ProviderError",
                    ex.Message,
                    ex.ErrorCode.ToString(),
                    ex.ProviderName), statusCode: statusCode);
            }
            catch (Exception)
            {
                return Results.Json(new AiErrorResponse(
                    "InternalError",
                    "An unexpected error occurred while processing the AI request",
                    "AI_INTERNAL_ERROR"), statusCode: StatusCodes.Status500InternalServerError);
            }
        })
        .RequireAuthorization(OpsBridgeAuthPolicies.OperatorOrAdmin)
        .Produces<AiChatResponseDto>(StatusCodes.Status200OK)
        .Produces<AiErrorResponse>(StatusCodes.Status400BadRequest)
        .Produces<AiErrorResponse>(StatusCodes.Status401Unauthorized)
        .Produces<AiErrorResponse>(StatusCodes.Status403Forbidden)
        .Produces<AiErrorResponse>(StatusCodes.Status429TooManyRequests)
        .Produces<AiErrorResponse>(StatusCodes.Status500InternalServerError)
        .Produces<AiErrorResponse>(StatusCodes.Status502BadGateway)
        .Produces<AiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
        .Produces<AiErrorResponse>(StatusCodes.Status504GatewayTimeout);

        // GET /api/v1/ai/health - Check AI service health
        group.MapGet("/health", async (
            IEnumerable<IAiProvider> providers,
            CancellationToken ct) =>
        {
            var providerStatuses = new List<object>();

            foreach (var provider in providers)
            {
                var isHealthy = provider.ProviderName != "fallback"; // fallback is always "healthy" but not a real provider
                providerStatuses.Add(new
                {
                    Provider = provider.ProviderName,
                    Status = isHealthy ? "Available" : "Fallback",
                    IsPrimary = isHealthy
                });
            }

            return Results.Ok(new
            {
                Status = providerStatuses.Any(p => (bool)p.GetType().GetProperty("IsPrimary")!.GetValue(p)!) ? "Healthy" : "Degraded",
                Providers = providerStatuses
            });
        })
        .AllowAnonymous() // Health check can be public
        .Produces(StatusCodes.Status200OK);

        return app;
    }

    private static bool IsStaffRole(string role)
    {
        return role == OpsBridgeRoles.Admin
            || role == OpsBridgeRoles.FinanceReviewer
            || role == OpsBridgeRoles.Operator;
    }

    // DTOs
    public sealed record AiChatResponseDto(
        string Response,
        string ConversationId,
        string Provider,
        int TokensUsed,
        bool IsFallback);

    public sealed record AiErrorResponse(
        string Error,
        string Message,
        string ErrorCode,
        string? Provider = null);
}